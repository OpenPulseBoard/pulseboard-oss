module PulseBoard.Signup

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open Suave.ServerErrors
open PulseBoard.Tenancy
open PulseBoard.Audit

// Phase 7 #3 — public onboarding surface.
//
// Two endpoints, both unauthenticated by design (they are the front door
// before any key exists):
//
//   POST /api/signup          create tenant + admin api-key + return one-shot
//                             plaintext + wizard URL
//   GET  /api/wizard/snippets?lang=<l>&apiKey=<k>&host=<h>
//                             curated copy-paste snippets for the wizard UI
//
// Per-IP token-bucket rate limiter (5 signups / hour / IP) prevents random
// internet visitors from filling the tenant table; slug regex + reserved
// list prevents stomping on the meta/admin/system tenants used by the
// platform itself.

// -- helpers ----------------------------------------------------------------

let private readBody (req : HttpRequest) : string =
  if isNull req.rawForm || req.rawForm.Length = 0 then ""
  else Encoding.UTF8.GetString req.rawForm

let private jsonResp (status : int) (body : string) : WebPart =
  let writer =
    match status with
    | 200 -> OK
    | 201 -> Suave.Successful.CREATED
    | 400 -> BAD_REQUEST
    | 404 -> NOT_FOUND
    | 409 -> Suave.RequestErrors.CONFLICT
    | 429 -> Suave.RequestErrors.TOO_MANY_REQUESTS
    | _   -> INTERNAL_ERROR
  writer body >=> Writers.setMimeType "application/json"

let private errJson (status : int) (msg : string) : WebPart =
  jsonResp status (sprintf """{"error":%s}""" (JsonSerializer.Serialize msg))

let private tryParseJson (body : string) : JsonDocument option =
  if String.IsNullOrWhiteSpace body then None
  else try Some (JsonDocument.Parse body) with _ -> None

let private tryGetString (el : JsonElement) (name : string) : string option =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.String ->
    let s = v.GetString()
    if String.IsNullOrWhiteSpace s then None else Some (s.Trim())
  | _ -> None

// -- slug validation --------------------------------------------------------

let private slugRegex =
  Regex(@"^[a-z][a-z0-9-]{2,31}$", RegexOptions.Compiled)

let private reservedSlugs =
  set [ "__meta__"; "admin"; "system"; "root"; "pulseboard"; "api"; "health" ]

let private slugErr (s : string) : string option =
  if not (slugRegex.IsMatch s)             then Some "slug must match ^[a-z][a-z0-9-]{2,31}$"
  elif reservedSlugs.Contains s            then Some "slug is reserved"
  elif s.StartsWith "-" || s.EndsWith "-"  then Some "slug must not start/end with '-'"
  else None

// -- per-IP rate limiter ----------------------------------------------------
//
// Simple bucket per IP keyed by string. `windowSec` = 3600, `maxPerWindow`
// = 5. Eviction happens lazily inside `tryConsume` when a window expires.

[<NoComparison; NoEquality>]
type private Bucket = { mutable count : int; mutable resetAt : DateTimeOffset }

type SignupRateLimiter (maxPerWindow : int, windowSec : int) =
  let buckets = ConcurrentDictionary<string, Bucket>()
  let win () = TimeSpan.FromSeconds(float windowSec)

  member _.TryConsume (ip : string) : Result<int, DateTimeOffset> =
    let now = DateTimeOffset.UtcNow
    let b = buckets.GetOrAdd(ip, fun _ -> { count = 0; resetAt = now + win () })
    lock b (fun () ->
      if now >= b.resetAt then
        b.count <- 0
        b.resetAt <- now + win ()
      if b.count >= maxPerWindow then
        Result.Error b.resetAt
      else
        b.count <- b.count + 1
        Result.Ok (maxPerWindow - b.count))

let private clientIp (ctx : HttpContext) : string =
  let fwd =
    ctx.request.headers
    |> Seq.tryFind (fun (k, _) ->
         String.Equals(k, "x-forwarded-for", StringComparison.OrdinalIgnoreCase))
    |> Option.map (snd >> fun v -> v.Trim())
  match fwd with
  | Some v when not (String.IsNullOrWhiteSpace v) ->
    v.Split(',').[0].Trim()
  | _ ->
    try ctx.clientIpTrustProxy.ToString()
    with _ -> "unknown"

// -- audit helper -----------------------------------------------------------

let private audit (log : IAuditLog) (action : string) (outcome : Outcome)
                  (ctx : HttpContext) (details : string option) =
  let ev : AuditEvent =
    { ts       = DateTimeOffset.UtcNow
      tenant   = None
      apiKeyId = None
      action   = action
      resource = ctx.request.path
      outcome  = outcome
      remoteIp = Some (clientIp ctx)
      details  = details }
  try log.Append ev with _ -> ()

// -- POST /api/signup -------------------------------------------------------

let private signup (store : ITenantStore) (limiter : SignupRateLimiter)
                   (log : IAuditLog) : WebPart =
  fun ctx -> async {
    let ip = clientIp ctx
    match limiter.TryConsume ip with
    | Result.Error resetAt ->
      audit log "signup" Deny ctx
        (Some (sprintf "ip=%s rate-limited until %s" ip (resetAt.ToString("o"))))
      return! errJson 429 "rate limit exceeded; try again later" ctx
    | Result.Ok _remaining ->
      match tryParseJson (readBody ctx.request) with
      | None ->
        audit log "signup" Deny ctx (Some "invalid json")
        return! errJson 400 "invalid JSON body" ctx
      | Some doc ->
        use _ = doc
        let root = doc.RootElement
        match tryGetString root "slug", tryGetString root "email" with
        | None, _ ->
          audit log "signup" Deny ctx (Some "missing slug")
          return! errJson 400 "field 'slug' is required" ctx
        | _, None ->
          audit log "signup" Deny ctx (Some "missing email")
          return! errJson 400 "field 'email' is required" ctx
        | Some slug, Some email ->
          match slugErr slug with
          | Some e ->
            audit log "signup" Deny ctx (Some (sprintf "bad slug=%s (%s)" slug e))
            return! errJson 400 e ctx
          | None ->
            // Idempotent on slug to make the front-end "click again" case
            // safe; returns 409 when the slug exists but no key was issued
            // by us (caller should pick a different slug).
            match store.TryGetTenantBySlug slug with
            | Some _ ->
              audit log "signup" Deny ctx
                (Some (sprintf "slug=%s already exists" slug))
              return! errJson 409 "slug already taken" ctx
            | None ->
              try
                let t = store.CreateTenant slug
                let issued =
                  store.IssueApiKey(
                    t.id,
                    sprintf "onboarding (%s)" email,
                    Admin,
                    Scope.Ingest ||| Scope.Query ||| Scope.Admin)
                let (TenantId tid)  = t.id
                let (ApiKeyId kid)  = issued.record.id
                audit log "signup" Allow ctx
                  (Some (sprintf "tenantId=%s slug=%s apiKeyId=%s email=%s"
                                 tid slug kid email))
                let key = issued.plaintext
                let body =
                  sprintf
                    """{"tenantId":%s,"slug":%s,"plan":"%s","apiKey":%s,"apiKeyId":%s,"wizardUrl":%s,"warning":"plaintext apiKey is shown once and cannot be recovered"}"""
                    (JsonSerializer.Serialize tid)
                    (JsonSerializer.Serialize slug)
                    (planToText t.plan)
                    (JsonSerializer.Serialize key)
                    (JsonSerializer.Serialize kid)
                    (JsonSerializer.Serialize (sprintf "/onboard?key=%s&tenant=%s"
                                                   (Uri.EscapeDataString key)
                                                   (Uri.EscapeDataString tid)))
                return! jsonResp 201 body ctx
              with ex ->
                audit log "signup" Error ctx (Some ex.Message)
                return! errJson 500 ex.Message ctx
  }

// -- GET /api/wizard/snippets ----------------------------------------------
//
// Returns a per-language batch of copy-paste blocks. We bake host + key
// into the snippet text on the server so the wizard UI is fully static.
// Languages: node | python | go | java | otel | prom | docker | k8s | curl.

let private tryGetQuery (ctx : HttpContext) (name : string) : string option =
  match ctx.request.queryParam name with
  | Choice1Of2 v when not (String.IsNullOrWhiteSpace v) -> Some v
  | _ -> None

let private snippetsFor (host : string) (key : string) (lang : string) =
  // Plaintext templates. Each `(title, code)` tuple is JSON-serialized
  // verbatim — Utf8JsonWriter handles escaping for us.
  match lang with
  | "node" ->
    [|
      "install",
        "npm install @pulseboard/sdk"
      "init",
        sprintf "import { PulseBoard } from '@pulseboard/sdk';\n\nconst pb = new PulseBoard({ host: '%s', apiKey: '%s' });\npb.counter('requests_total').inc();" host key
    |]
  | "python" ->
    [|
      "install", "pip install pulseboard"
      "init",
        sprintf "from pulseboard import PulseBoard\n\npb = PulseBoard(host='%s', api_key='%s')\npb.counter('requests_total').inc()" host key
    |]
  | "go" ->
    [|
      "install", "go get github.com/pulseboard/pulseboard-go"
      "init",
        sprintf "import \"github.com/pulseboard/pulseboard-go\"\n\nclient := pulseboard.New(\"%s\", \"%s\")\nclient.Counter(\"requests_total\").Inc()" host key
    |]
  | "java" ->
    [|
      "maven",
        "<dependency>\n  <groupId>com.pulseboard</groupId>\n  <artifactId>pulseboard-client</artifactId>\n  <version>1.0.0</version>\n</dependency>"
      "init",
        sprintf "PulseBoard pb = new PulseBoard(\"%s\", \"%s\");\npb.counter(\"requests_total\").inc();" host key
    |]
  | "otel" ->
    [|
      "endpoint",
        sprintf "OTEL_EXPORTER_OTLP_ENDPOINT=%s\nOTEL_EXPORTER_OTLP_HEADERS=Authorization=Bearer %s" host key
    |]
  | "prom" ->
    [|
      "remote_write",
        sprintf "remote_write:\n  - url: %s/api/v1/write\n    authorization:\n      type: Bearer\n      credentials: %s" host key
    |]
  | "docker" ->
    [|
      "run",
        sprintf "docker run -e PULSEBOARD_HOST=%s -e PULSEBOARD_API_KEY=%s your-app" host key
    |]
  | "k8s" ->
    [|
      "secret",
        sprintf "apiVersion: v1\nkind: Secret\nmetadata:\n  name: pulseboard\nstringData:\n  PULSEBOARD_HOST: %s\n  PULSEBOARD_API_KEY: %s" host key
    |]
  | "curl" | _ ->
    [|
      "ingest-metric",
        sprintf "curl -X POST %s/ingest/metrics \\\n  -H 'Authorization: Bearer %s' \\\n  -H 'Content-Type: application/json' \\\n  -d '{\"name\":\"requests_total\",\"value\":1,\"labels\":{\"service\":\"demo\"}}'" host key
      "ingest-log",
        sprintf "curl -X POST %s/ingest/logs \\\n  -H 'Authorization: Bearer %s' \\\n  -H 'Content-Type: application/json' \\\n  -d '{\"msg\":\"hello pulseboard\",\"level\":\"info\"}'" host key
    |]

let private wizardSnippets : WebPart =
  fun ctx -> async {
    let lang = tryGetQuery ctx "lang" |> Option.defaultValue "curl"
    let host = tryGetQuery ctx "host" |> Option.defaultValue "https://pulseboard.local"
    let key  = tryGetQuery ctx "apiKey" |> Option.defaultValue "<API_KEY>"
    let snippets = snippetsFor host key lang
    let bytes =
      use ms = new System.IO.MemoryStream()
      (
        use w = new Utf8JsonWriter(ms)
        w.WriteStartObject()
        w.WriteString("lang", lang)
        w.WriteString("host", host)
        w.WritePropertyName("snippets")
        w.WriteStartArray()
        for title, code in snippets do
          w.WriteStartObject()
          w.WriteString("title", title)
          w.WriteString("code",  code)
          w.WriteEndObject()
        w.WriteEndArray()
        w.WriteEndObject()
      )
      ms.ToArray()
    return!
      jsonResp 200 (Encoding.UTF8.GetString bytes) ctx
  }

// -- module entry point -----------------------------------------------------

/// Default-shaped rate limiter (5 signups/hour/IP).
let defaultLimiter () = SignupRateLimiter(5, 3600)

/// Public onboarding routes. Mount BEFORE any tenant gate so the
/// unauthenticated front door is reachable.
let webPart (store : ITenantStore) (limiter : SignupRateLimiter)
            (log : IAuditLog) : WebPart =
  choose [
    POST >=> path "/api/signup"           >=> signup store limiter log
    GET  >=> path "/api/wizard/snippets"  >=> wizardSnippets
  ]

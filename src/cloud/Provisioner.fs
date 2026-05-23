module PulseBoard.Provisioner

open System
open System.Collections.Concurrent
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open Suave.ServerErrors

// Phase 9 — per-customer workspace provisioner.
//
// The marketing site (`pulseboard.cloud`, run with `--site-only`) forwards
// signup POSTs to this service. The provisioner:
//   1. allocates a slug (the user's choice, possibly suffixed for uniqueness),
//   2. spawns a Fly Machine running the PulseBoard binary in
//      `--multi-tenant` mode for that customer,
//   3. records the slug → upstream mapping in a registry,
//   4. issues a bootstrap API key by calling /api/signup on the new
//      workspace once it's healthy,
//   5. returns `{url, apiKey, tenantId}` to the marketing site.
//
// Caddy on-demand TLS sits in front of every `*.pulseboard.cloud` host. It
// asks `GET /provision/ask?domain=<host>` whether to mint a cert (200 if
// known, 404 otherwise) and `GET /provision/route?domain=<host>` for the
// upstream URL to reverse_proxy to.
//
// The Fly client has a real HTTP implementation against api.machines.dev
// and a dry-run impl that just logs the intent and returns a synthetic
// hostname — that lets us smoke-test the flow end-to-end without
// credentials.

// -- types ------------------------------------------------------------------

type WorkspaceRecord =
  { slug         : string
    flyAppName   : string
    upstreamUrl  : string   // what Caddy / the router should proxy to
    tenantId     : string option
    apiKeyId     : string option
    ownerEmail   : string
    createdAt    : DateTimeOffset }

type IWorkspaceRegistry =
  abstract Insert : WorkspaceRecord -> unit
  abstract TryGetBySlug : string -> WorkspaceRecord option
  abstract TryGetByHost : string -> WorkspaceRecord option
  abstract Update : string -> (WorkspaceRecord -> WorkspaceRecord) -> unit

type InMemoryWorkspaceRegistry () =
  let bySlug = ConcurrentDictionary<string, WorkspaceRecord>()
  interface IWorkspaceRegistry with
    member _.Insert r = bySlug.[r.slug] <- r
    member _.TryGetBySlug s =
      match bySlug.TryGetValue s with true, r -> Some r | _ -> None
    member _.TryGetByHost h =
      // host is like "<slug>.pulseboard.cloud" — strip the apex if present.
      let s =
        let idx = h.IndexOf '.'
        if idx > 0 then h.Substring(0, idx).ToLowerInvariant()
        else h.ToLowerInvariant()
      match bySlug.TryGetValue s with true, r -> Some r | _ -> None
    member _.Update s f =
      bySlug.AddOrUpdate(s, (fun _ -> failwithf "no such slug: %s" s),
                             (fun _ old -> f old)) |> ignore

// -- Fly Machines client ----------------------------------------------------

type FlyMachineConfig =
  { image       : string                  // e.g. registry.fly.io/pulseboard1:latest
    region      : string                  // e.g. "iad"
    envExtra    : Map<string, string>     // merged with built-in defaults
    sizeCpus    : int                     // shared CPUs
    sizeMemMb   : int }

type ProvisionedWorkspace =
  { appName     : string
    publicUrl   : string                  // https://<app>.fly.dev — the upstream Caddy will proxy to
    internalUrl : string }                // https://<app>.internal — Fly private net

type IFlyClient =
  abstract CreateWorkspace : slug:string * ownerEmail:string * cfg:FlyMachineConfig -> Async<ProvisionedWorkspace>

type DryRunFlyClient () =
  interface IFlyClient with
    member _.CreateWorkspace (slug, email, cfg) = async {
      // Simulate latency so the proxy path is exercised.
      do! Async.Sleep 50
      eprintfn "  [provisioner/dry-run] would create Fly app slug=%s email=%s image=%s region=%s cpus=%d mem=%d"
        slug email cfg.image cfg.region cfg.sizeCpus cfg.sizeMemMb
      return
        { appName     = sprintf "pb-%s" slug
          publicUrl   = sprintf "https://pb-%s.fly.dev" slug
          internalUrl = sprintf "http://pb-%s.internal:8080" slug }
    }

/// Real HTTP client against the Fly Machines REST API (api.machines.dev/v1).
/// Configurable via `FLY_API_TOKEN`, `FLY_ORG_SLUG`. Each provision creates
/// a fresh app (`pb-<slug>`) and one Machine running the configured image
/// in `--multi-tenant` mode. The Machine config exposes port 8080 publicly
/// over HTTPS so Caddy's on-demand TLS can reverse_proxy to it.
type HttpFlyClient (token : string, orgSlug : string) =
  let http = new HttpClient(BaseAddress = Uri "https://api.machines.dev/")
  do http.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", token)

  let postJson (path : string) (body : string) = async {
    use content = new StringContent(body, Encoding.UTF8, "application/json")
    let! resp = http.PostAsync(path, content) |> Async.AwaitTask
    let! txt = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
    if not resp.IsSuccessStatusCode then
      failwithf "fly API %s -> %d: %s" path (int resp.StatusCode) txt
    return txt
  }

  interface IFlyClient with
    member _.CreateWorkspace (slug, email, cfg) = async {
      let appName = sprintf "pb-%s" slug
      // 1. Create the app.
      let appBody = sprintf """{"app_name":%s,"org_slug":%s}"""
                      (JsonSerializer.Serialize appName)
                      (JsonSerializer.Serialize orgSlug)
      let! _ = postJson "/v1/apps" appBody
      // 2. Create one Machine with our binary.
      let envMap =
        cfg.envExtra
        |> Map.add "PULSE_MULTI_TENANT" "1"
        |> Map.add "PULSE_OWNER_EMAIL" email
        |> Map.add "PULSE_SLUG" slug
      let envJson =
        envMap
        |> Map.toSeq
        |> Seq.map (fun (k, v) ->
             sprintf "%s:%s"
               (JsonSerializer.Serialize k)
               (JsonSerializer.Serialize v))
        |> String.concat ","
      let machineBody =
        sprintf """{"name":%s,"region":%s,"config":{"image":%s,"env":{%s},"services":[{"protocol":"tcp","internal_port":8080,"ports":[{"port":80,"handlers":["http"]},{"port":443,"handlers":["tls","http"]}]}],"guest":{"cpu_kind":"shared","cpus":%d,"memory_mb":%d}}}"""
          (JsonSerializer.Serialize (appName + "-0"))
          (JsonSerializer.Serialize cfg.region)
          (JsonSerializer.Serialize cfg.image)
          envJson
          cfg.sizeCpus
          cfg.sizeMemMb
      let! _ = postJson (sprintf "/v1/apps/%s/machines" appName) machineBody
      return
        { appName     = appName
          publicUrl   = sprintf "https://%s.fly.dev" appName
          internalUrl = sprintf "http://%s.internal:8080" appName }
    }

  interface IDisposable with
    member _.Dispose () = http.Dispose ()

// -- bootstrap the new workspace -------------------------------------------
//
// After Fly says the Machine is up, we still need to mint the first API
// key on that workspace. We do that by calling its own /api/signup once.
// The workspace must be running in --multi-tenant mode (which our spawn
// config sets via PULSE_MULTI_TENANT). The bootstrap call is just a
// regular POST; the workspace has no prior keys so it answers normally.
//
// TODO: harden this. A real implementation would (a) gate /api/signup
// behind a single-use bootstrap token passed via env at machine create,
// and (b) flip a "bootstrapped=true" bit so subsequent /api/signup calls
// require operator action. For first cut we accept the small window
// between machine boot and our own POST.

type BootstrapResult =
  { tenantId : string
    apiKeyId : string
    apiKey   : string }

let private bootstrapWorkspace (http : HttpClient) (baseUrl : string)
                               (slug : string) (email : string)
                               : Async<BootstrapResult> = async {
  let body = sprintf """{"slug":%s,"email":%s}"""
               (JsonSerializer.Serialize slug)
               (JsonSerializer.Serialize email)
  use content = new StringContent(body, Encoding.UTF8, "application/json")
  let url = baseUrl.TrimEnd('/') + "/api/signup"
  let! resp = http.PostAsync(url, content) |> Async.AwaitTask
  let! txt = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
  if not resp.IsSuccessStatusCode then
    failwithf "workspace bootstrap %s -> %d: %s" url (int resp.StatusCode) txt
  use doc = JsonDocument.Parse txt
  let r = doc.RootElement
  let g (name : string) = r.GetProperty(name).GetString()
  return { tenantId = g "tenantId"; apiKeyId = g "apiKeyId"; apiKey = g "apiKey" }
}

// In dry-run mode we mint a synthetic key so the marketing flow can
// display something coherent in tests without actually reaching a Fly
// Machine that doesn't exist.
let private bootstrapDryRun (slug : string) : BootstrapResult =
  let rnd = Guid.NewGuid().ToString("N").Substring(0, 12)
  { tenantId = sprintf "tenant_%s" rnd
    apiKeyId = sprintf "key_%s" (rnd.Substring(0, 8))
    apiKey   = sprintf "pk_%s.%s" rnd (Guid.NewGuid().ToString("N")) }

// -- config + state ---------------------------------------------------------

type ProvisionerConfig =
  { fly           : IFlyClient
    dryRun        : bool         // when true, skip workspace bootstrap HTTP call
    registry      : IWorkspaceRegistry
    rootDomain    : string       // e.g. "pulseboard.cloud"
    machineConfig : FlyMachineConfig }

// -- HTTP surface -----------------------------------------------------------

let private readBody (req : HttpRequest) =
  if isNull req.rawForm || req.rawForm.Length = 0 then ""
  else Encoding.UTF8.GetString req.rawForm

let private jsonResp (status : int) (body : string) : WebPart =
  let writer =
    match status with
    | 200 -> OK | 201 -> Suave.Successful.CREATED
    | 400 -> BAD_REQUEST | 404 -> NOT_FOUND | 409 -> Suave.RequestErrors.CONFLICT
    | _   -> INTERNAL_ERROR
  writer body >=> Writers.setMimeType "application/json"

let private errJson status msg =
  jsonResp status (sprintf """{"error":%s}""" (JsonSerializer.Serialize (msg : string)))

let private slugOk (s : string) =
  let re = System.Text.RegularExpressions.Regex(@"^[a-z][a-z0-9-]{2,31}$")
  re.IsMatch s && not (s.StartsWith "-") && not (s.EndsWith "-")

/// POST /api/provision  body: {"slug":"acme","email":"a@b.co"}
let private provision (cfg : ProvisionerConfig) (httpForBootstrap : HttpClient) : WebPart =
  fun ctx -> async {
    try
      let body = readBody ctx.request
      use doc = JsonDocument.Parse (if String.IsNullOrWhiteSpace body then "{}" else body)
      let root = doc.RootElement
      let tryStr (n : string) =
        match root.TryGetProperty n with
        | true, v when v.ValueKind = JsonValueKind.String ->
          let s = v.GetString().Trim() in if s = "" then None else Some s
        | _ -> None
      match tryStr "slug", tryStr "email" with
      | None, _ -> return! errJson 400 "field 'slug' is required" ctx
      | _, None -> return! errJson 400 "field 'email' is required" ctx
      | Some rawSlug, Some email ->
        let slug = rawSlug.ToLowerInvariant()
        if not (slugOk slug) then
          return! errJson 400 "slug must match ^[a-z][a-z0-9-]{2,31}$ and not start/end with '-'" ctx
        else
          // Append a 4-hex suffix on collision so retries succeed.
          let finalSlug =
            match cfg.registry.TryGetBySlug slug with
            | None -> slug
            | Some _ ->
              let salt = Guid.NewGuid().ToString("N").Substring(0, 4)
              sprintf "%s-%s" slug salt
          let! ws = cfg.fly.CreateWorkspace(finalSlug, email, cfg.machineConfig)
          // Record before bootstrap so /provision/ask works as soon as
          // Caddy receives the first cert request.
          let host = sprintf "%s.%s" finalSlug cfg.rootDomain
          let record0 =
            { slug = finalSlug; flyAppName = ws.appName
              upstreamUrl = ws.publicUrl
              tenantId = None; apiKeyId = None
              ownerEmail = email; createdAt = DateTimeOffset.UtcNow }
          cfg.registry.Insert record0
          // Bootstrap the workspace key. In dry-run we synthesise one.
          let! boot =
            if cfg.dryRun then async { return bootstrapDryRun finalSlug }
            else bootstrapWorkspace httpForBootstrap ws.publicUrl finalSlug email
          cfg.registry.Update finalSlug (fun r ->
            { r with tenantId = Some boot.tenantId; apiKeyId = Some boot.apiKeyId })
          let publicHostUrl = sprintf "https://%s" host
          let resp =
            sprintf """{"slug":%s,"url":%s,"tenantId":%s,"apiKey":%s,"apiKeyId":%s}"""
              (JsonSerializer.Serialize finalSlug)
              (JsonSerializer.Serialize publicHostUrl)
              (JsonSerializer.Serialize boot.tenantId)
              (JsonSerializer.Serialize boot.apiKey)
              (JsonSerializer.Serialize boot.apiKeyId)
          return! jsonResp 201 resp ctx
    with ex ->
      eprintfn "  [provisioner] %s" ex.Message
      return! errJson 500 ex.Message ctx
  }

let private askOrRoute (cfg : ProvisionerConfig) (isAsk : bool) : WebPart =
  fun ctx -> async {
    let host =
      match ctx.request.queryParam "domain" with
      | Choice1Of2 v -> v.Trim().ToLowerInvariant()
      | _ -> ""
    if host = "" then return! errJson 400 "missing ?domain=" ctx
    elif host = cfg.rootDomain then
      // Apex host — Caddy may legitimately ask about it (when site-only is
      // also fronted by the same Caddy). Answer "yes" so it gets a cert.
      if isAsk then return! OK "" ctx
      else return! jsonResp 200 """{"upstream":"http://localhost:8080"}""" ctx
    else
      match cfg.registry.TryGetByHost host with
      | None -> return! errJson 404 (sprintf "unknown host: %s" host) ctx
      | Some r ->
        if isAsk then return! OK "" ctx
        else
          let body = sprintf """{"upstream":%s}""" (JsonSerializer.Serialize r.upstreamUrl)
          return! jsonResp 200 body ctx
  }

let webPart (cfg : ProvisionerConfig) : WebPart =
  // One shared HttpClient for the workspace bootstrap call. Created once
  // here so we don't churn sockets on every signup.
  let http = new HttpClient(Timeout = TimeSpan.FromSeconds 30.0)
  choose [
    GET >=> path "/healthz" >=>
      (OK """{"status":"ok","role":"provisioner"}"""
       >=> Writers.setMimeType "application/json")
    POST >=> path "/api/provision" >=> provision cfg http
    GET  >=> path "/provision/ask"   >=> askOrRoute cfg true
    GET  >=> path "/provision/route" >=> askOrRoute cfg false
  ]

/// Run a standalone provisioner service. Returns once the server exits.
let run (port : int) (cfg : ProvisionerConfig) : unit =
  // Accepts a comma-separated list, e.g. PULSE_BIND_ADDR="::,0.0.0.0".
  // See the matching block in Program.fs / SiteOnly.fs for the rationale
  // (.NET on Linux defaults AF_INET6 sockets to IPV6_V6ONLY=1, so a
  // single `::` listener does NOT accept the IPv4 loopback that Fly's
  // health check uses; we need both an IPv6 and an IPv4 binding to be
  // truly dual-stack).
  let bindAddrs =
    match Environment.GetEnvironmentVariable "PULSE_BIND_ADDR" with
    | null | "" -> [ IPAddress.Loopback ]
    | s ->
      s.Split([| ',' ; ';' ; ' ' |], StringSplitOptions.RemoveEmptyEntries)
      |> Array.choose (fun raw ->
           let t = raw.Trim()
           match IPAddress.TryParse t with
           | true, ip -> Some ip
           | _ ->
             eprintfn "  [WARN] PULSE_BIND_ADDR entry %s is not a valid IP; ignoring" t
             None)
      |> Array.toList
      |> function
         | []  ->
           eprintfn "  [WARN] PULSE_BIND_ADDR=%s yielded no valid IPs; falling back to 127.0.0.1" s
           [ IPAddress.Loopback ]
         | ips -> ips
  let config =
    { defaultConfig with
        bindings = bindAddrs |> List.map (fun ip -> HttpBinding.create HTTP ip (uint16 port)) }
  for ip in bindAddrs do
    printfn "PulseBoard (provisioner) listening on http://%O:%d" ip port
  printfn "  Root domain: %s" cfg.rootDomain
  printfn "  Fly client:  %s" (if cfg.dryRun then "dry-run (no API calls)" else "live (api.machines.dev)")
  printfn "  Image:       %s (region %s)" cfg.machineConfig.image cfg.machineConfig.region
  startWebServer config (webPart cfg)

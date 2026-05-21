module PulseBoard.PromScrape

open System
open System.Collections.Concurrent
open System.Globalization
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open Suave.ServerErrors
open PulseBoard.TimeSeries
open PulseBoard.Tenancy
open PulseBoard.Quotas
open PulseBoard.Audit
open PulseBoard.Gateway
open PulseBoard.Ingest

// Prometheus scrape mode (PLAN.md Phase 2 step 3). A tenant registers a
// `ScrapeTarget` (URL + interval + optional bearer token + optional extra
// labels) and a background worker periodically GETs each target's
// `/metrics`, parses the Prometheus text exposition format, and writes
// each sample through the same MetricStore path as remote_write so
// alerts, queries, cardinality admission and per-tenant accounting all
// see one namespace.
//
// Why text-format only: it's what 99% of /metrics endpoints emit (the
// OpenMetrics binary variant is rarely deployed). The Prom text grammar
// is small enough to parse by hand; pulling in a heavy client library
// would buy us nothing.

// -- model -------------------------------------------------------------------

type ScrapeTarget =
  { id              : string
    tenantId        : TenantId
    url             : string
    intervalSec     : int
    /// Labels merged into every series produced by this target. Equivalent
    /// to Prom's `external_labels` / per-job static labels. Target's own
    /// labels win on collision (honor_labels = false default).
    extraLabels     : Map<string, string>
    bearerToken     : string option
    createdAt       : DateTimeOffset }

/// Mutable per-target status snapshot. Kept as a record cell so the
/// scraper can swap in a fresh value atomically.
[<NoComparison; NoEquality>]
type ScrapeStatus =
  { lastScrapeAt    : DateTimeOffset option
    nextDueAt       : DateTimeOffset
    lastSampleCount : int
    lastDurationMs  : int
    lastError       : string option }

let private emptyStatus (now : DateTimeOffset) : ScrapeStatus =
  { lastScrapeAt = None; nextDueAt = now
    lastSampleCount = 0; lastDurationMs = 0; lastError = None }

// -- in-memory repo ----------------------------------------------------------

type IScrapeRepo =
  abstract member List       : TenantId -> ScrapeTarget array
  abstract member ListAll    : unit -> ScrapeTarget array
  abstract member TryGet     : string -> ScrapeTarget option
  abstract member Upsert     : ScrapeTarget -> unit
  abstract member Delete     : string -> bool
  abstract member Status     : string -> ScrapeStatus option
  abstract member SetStatus  : string * ScrapeStatus -> unit

type InMemoryScrapeRepo() =
  let targets = ConcurrentDictionary<string, ScrapeTarget>()
  let statuses = ConcurrentDictionary<string, ScrapeStatus>()
  interface IScrapeRepo with
    member _.List (tid : TenantId) =
      targets.Values
      |> Seq.filter (fun t -> t.tenantId = tid)
      |> Seq.sortBy (fun t -> t.createdAt)
      |> Seq.toArray
    member _.ListAll () =
      targets.Values |> Seq.toArray
    member _.TryGet (id : string) =
      match targets.TryGetValue id with
      | true, t -> Some t
      | _ -> None
    member _.Upsert (t : ScrapeTarget) =
      targets.[t.id] <- t
      statuses.TryAdd(t.id, emptyStatus DateTimeOffset.UtcNow) |> ignore
    member _.Delete (id : string) =
      let removed, _ = targets.TryRemove id
      statuses.TryRemove id |> ignore
      removed
    member _.Status (id : string) =
      match statuses.TryGetValue id with
      | true, s -> Some s
      | _ -> None
    member _.SetStatus (id, s) =
      statuses.[id] <- s

// -- text exposition parser --------------------------------------------------

let private nameLabel = "__name__"

[<Struct>] type private Label  = { name : string; value : string }
[<Struct>] type private Sample = { metric : string; labels : Label[]
                                   value : float; tsMs : int64 voption }

/// Parse a single non-comment Prometheus text line:
///   `metric_name{l1="v1",l2="v2"} 1.23 1700000000000`
/// Labels block + timestamp are optional. Returns None for malformed lines
/// (the rest of the response continues to parse).
let private tryParseLine (line : string) : Sample voption =
  let line = line.TrimStart()
  if line.Length = 0 || line.[0] = '#' then ValueNone
  else
    let mutable i = 0
    // metric name: [a-zA-Z_:][a-zA-Z0-9_:]*
    let nameStart = i
    while i < line.Length &&
          (let c = line.[i]
           c = '_' || c = ':' || Char.IsLetterOrDigit c) do
      i <- i + 1
    if i = nameStart then ValueNone
    else
    let metric = line.Substring(nameStart, i - nameStart)
    let labels = ResizeArray<Label>()
    // optional `{...}`
    if i < line.Length && line.[i] = '{' then
      i <- i + 1
      // parse k="v"(,k="v")* until `}`
      let mutable broken = false
      while not broken && i < line.Length && line.[i] <> '}' do
        // skip ws
        while i < line.Length && line.[i] = ' ' do i <- i + 1
        let kStart = i
        while i < line.Length &&
              (let c = line.[i]
               c = '_' || Char.IsLetterOrDigit c) do
          i <- i + 1
        if i = kStart then broken <- true
        else
          let k = line.Substring(kStart, i - kStart)
          // expect '='
          while i < line.Length && line.[i] = ' ' do i <- i + 1
          if i >= line.Length || line.[i] <> '=' then broken <- true
          else
            i <- i + 1
            while i < line.Length && line.[i] = ' ' do i <- i + 1
            if i >= line.Length || line.[i] <> '"' then broken <- true
            else
              i <- i + 1
              let sb = StringBuilder()
              let mutable closed = false
              while not closed && i < line.Length do
                let c = line.[i]
                if c = '\\' && i + 1 < line.Length then
                  let n = line.[i + 1]
                  let unescaped =
                    match n with
                    | '\\' -> '\\'
                    | '"'  -> '"'
                    | 'n'  -> '\n'
                    | 't'  -> '\t'
                    | _    -> n
                  sb.Append unescaped |> ignore
                  i <- i + 2
                elif c = '"' then
                  closed <- true
                  i <- i + 1
                else
                  sb.Append c |> ignore
                  i <- i + 1
              if not closed then broken <- true
              else
                labels.Add { name = k; value = sb.ToString() }
                // optional ',' or end
                while i < line.Length && line.[i] = ' ' do i <- i + 1
                if i < line.Length && line.[i] = ',' then i <- i + 1
      if broken || i >= line.Length || line.[i] <> '}' then
        ()  // fall through; caller checks
      else
        i <- i + 1  // consume '}'
    // skip ws
    while i < line.Length && line.[i] = ' ' do i <- i + 1
    if i >= line.Length then ValueNone
    else
    // value: float, NaN, +Inf, -Inf
    let valStart = i
    while i < line.Length && line.[i] <> ' ' && line.[i] <> '\t' do
      i <- i + 1
    let valStr = line.Substring(valStart, i - valStart)
    let parseFloat (s : string) : float voption =
      match s with
      | "NaN"  | "nan"  -> ValueSome Double.NaN
      | "+Inf" | "Inf"  | "+inf" | "inf" -> ValueSome Double.PositiveInfinity
      | "-Inf" | "-inf" -> ValueSome Double.NegativeInfinity
      | _ ->
        let ok, v = Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture)
        if ok then ValueSome v else ValueNone
    match parseFloat valStr with
    | ValueNone -> ValueNone
    | ValueSome v ->
      // optional timestamp
      while i < line.Length && (line.[i] = ' ' || line.[i] = '\t') do
        i <- i + 1
      let ts =
        if i >= line.Length then ValueNone
        else
          let tsStr = line.Substring(i).TrimEnd()
          let ok, n = Int64.TryParse(tsStr, NumberStyles.Integer, CultureInfo.InvariantCulture)
          if ok then ValueSome n else ValueNone
      ValueSome { metric = metric; labels = labels.ToArray()
                  value = v; tsMs = ts }

let private parseExposition (body : string) : Sample array =
  let acc = ResizeArray<Sample>()
  let lines = body.Split([| '\n' |])
  for raw in lines do
    let line =
      if raw.EndsWith "\r" then raw.Substring(0, raw.Length - 1) else raw
    match tryParseLine line with
    | ValueSome s -> acc.Add s
    | ValueNone   -> ()
  acc.ToArray()

// -- canonical series naming -------------------------------------------------
// Same Prom-style `name{k="v",...}` form (sorted labels, escaped) emitted
// by PromRemoteWrite, OTLP, and LokiPush so cardinality admission, alerts
// and queries see one namespace regardless of source.

let private canonicalName (metric : string) (labels : Label[]) : string =
  let others =
    labels
    |> Array.filter (fun l -> l.name <> nameLabel)
  if others.Length = 0 then metric
  else
    let sorted = Array.copy others
    Array.sortInPlaceBy (fun (l : Label) -> l.name) sorted
    let sb = StringBuilder(metric)
    sb.Append '{' |> ignore
    for i in 0 .. sorted.Length - 1 do
      if i > 0 then sb.Append ',' |> ignore
      let l = sorted.[i]
      sb.Append l.name |> ignore
      sb.Append "=\"" |> ignore
      for c in l.value do
        match c with
        | '\\' -> sb.Append "\\\\" |> ignore
        | '"'  -> sb.Append "\\\"" |> ignore
        | '\n' -> sb.Append "\\n"  |> ignore
        | _    -> sb.Append c       |> ignore
      sb.Append '"' |> ignore
    sb.Append '}' |> ignore
    sb.ToString()

/// Merge target's `extraLabels` with the per-sample labels. Target labels
/// do NOT override sample labels (mirrors Prom's default
/// `honor_labels: false` semantics).
let private mergeLabels (target : ScrapeTarget) (sample : Label[]) : Label[] =
  if Map.isEmpty target.extraLabels then sample
  else
    let have = sample |> Array.map (fun l -> l.name) |> Set.ofArray
    let extras =
      target.extraLabels
      |> Map.toSeq
      |> Seq.choose (fun (k, v) ->
          if Set.contains k have then None
          else Some { name = k; value = v })
      |> Seq.toArray
    Array.append sample extras

// -- audit helper ------------------------------------------------------------

let private auditScrape (log : IAuditLog) (target : ScrapeTarget)
                        (outcome : Outcome) (details : string) =
  let ev : AuditEvent =
    { ts       = DateTimeOffset.UtcNow
      tenant   = Some target.tenantId
      apiKeyId = None
      action   = "scrape"
      resource = target.url
      outcome  = outcome
      remoteIp = None
      details  = Some details }
  try log.Append ev with _ -> ()

// -- background scraper ------------------------------------------------------

[<NoComparison; NoEquality>]
type ScrapeDeps =
  { repo       : IScrapeRepo
    storage    : IStorageClient
    quotas     : IngestQuotas option
    httpClient : HttpClient }

let private scrapeOnce (deps : ScrapeDeps) (target : ScrapeTarget) : Async<unit> =
  async {
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let mutable err : string option = None
    let mutable accepted = 0
    try
      use req = new HttpRequestMessage(HttpMethod.Get, target.url)
      // Standard Prom Accept; some exporters branch on it.
      req.Headers.Add("Accept",
        "text/plain;version=0.0.4;q=0.5,application/openmetrics-text;q=0.4,*/*;q=0.1")
      req.Headers.Add("User-Agent", "PulseBoard/scrape")
      match target.bearerToken with
      | Some tok ->
        req.Headers.Authorization <-
          System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tok)
      | None -> ()
      let! resp =
        deps.httpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead)
        |> Async.AwaitTask
      use resp = resp
      if not resp.IsSuccessStatusCode then
        err <- Some (sprintf "HTTP %d" (int resp.StatusCode))
      else
        let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
        let parsed = parseExposition body
        let scrapeTsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        let samples = ResizeArray<MetricSample>()
        for s in parsed do
          let merged = mergeLabels target s.labels
          let name = canonicalName s.metric merged
          if name.Length > 0 && not (Double.IsNaN s.value) then
            let admit =
              match deps.quotas with
              | Some q ->
                match q.limiter.TryAdmitSeries(target.tenantId, name) with
                | CardinalityResult.Ok -> true
                | CardinalityResult.Rejected cap ->
                  auditScrape q.auditLog target Deny
                    (sprintf "cardinality cap=%d series=%s" cap name)
                  false
              | None -> true
            if admit then
              let ts =
                match s.tsMs with
                | ValueSome n -> n
                | ValueNone   -> scrapeTsMs
              samples.Add { seriesName = name; tsMs = ts; value = s.value }
        let (TenantId tidStr) = target.tenantId
        do! deps.storage.WriteMetricSamples(tidStr, samples)
        accepted <- samples.Count
    with ex ->
      err <- Some ex.Message
    sw.Stop()
    let now = DateTimeOffset.UtcNow
    let nextDue = now.AddSeconds(float target.intervalSec)
    deps.repo.SetStatus(target.id,
      { lastScrapeAt = Some now
        nextDueAt = nextDue
        lastSampleCount = accepted
        lastDurationMs = int sw.ElapsedMilliseconds
        lastError = err })
    // Light audit trail: one Allow per successful scrape (records sample
    // count so volume per target is auditable); errors recorded as Error.
    match deps.quotas, err with
    | Some q, Some msg ->
      auditScrape q.auditLog target Error
        (sprintf "%s after %dms" msg (int sw.ElapsedMilliseconds))
    | Some q, None ->
      auditScrape q.auditLog target Allow
        (sprintf "samples=%d duration=%dms" accepted (int sw.ElapsedMilliseconds))
    | None, _ -> ()
  }

/// Background scraper. Wakes every `tickIntervalSec` seconds, fans out
/// async GETs to every target whose `nextDueAt <= now`. Per-target work
/// runs on the thread pool so a slow target never blocks the loop.
type Scraper(deps : ScrapeDeps, ?tickIntervalSec : int) =
  let tickSec = defaultArg tickIntervalSec 1
  let mutable timer : Timer option = None

  let tick _ =
    try
      let now = DateTimeOffset.UtcNow
      for t in deps.repo.ListAll() do
        let due =
          match deps.repo.Status t.id with
          | Some s -> s.nextDueAt <= now
          | None   -> true
        if due then
          // Pre-mark nextDueAt so a long-running scrape doesn't re-fire on
          // the next tick. Final status overwrites on completion.
          deps.repo.SetStatus(t.id,
            { (deps.repo.Status t.id
               |> Option.defaultValue (emptyStatus now)) with
                nextDueAt = now.AddSeconds(float t.intervalSec) })
          Async.Start (scrapeOnce deps t)
    with _ -> ()

  member _.Start () =
    match timer with
    | Some _ -> ()
    | None ->
      timer <-
        Some (new Timer(TimerCallback(tick), null,
                        TimeSpan.FromSeconds(float tickSec),
                        TimeSpan.FromSeconds(float tickSec)))

  interface IDisposable with
    member _.Dispose () =
      match timer with
      | Some t -> t.Dispose(); timer <- None
      | None   -> ()

// -- admin webPart -----------------------------------------------------------

let private readBody (req : HttpRequest) : string =
  if isNull req.rawForm || req.rawForm.Length = 0 then ""
  else Encoding.UTF8.GetString req.rawForm

let private jsonResp (status : int) (body : string) : WebPart =
  match status with
  | 204 -> NO_CONTENT
  | _ ->
    let writer =
      match status with
      | 200 -> OK
      | 201 -> Suave.Successful.CREATED
      | 400 -> BAD_REQUEST
      | 404 -> NOT_FOUND
      | _   -> INTERNAL_ERROR
    writer body >=> Writers.setMimeType "application/json"

let private errJson (status : int) (msg : string) : WebPart =
  jsonResp status (sprintf """{"error":%s}""" (JsonSerializer.Serialize msg))

let private tryParseJson (body : string) =
  if String.IsNullOrWhiteSpace body then None
  else try Some (JsonDocument.Parse body) with _ -> None

let private tryGetString (el : JsonElement) (name : string) : string option =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.String ->
    let s = v.GetString()
    if String.IsNullOrWhiteSpace s then None else Some (s.Trim())
  | _ -> None

let private tryGetInt (el : JsonElement) (name : string) : int option =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.Number ->
    let ok, n = v.TryGetInt32()
    if ok then Some n else None
  | _ -> None

let private statusJson (s : ScrapeStatus) =
  let lastScrape =
    match s.lastScrapeAt with
    | Some ts -> sprintf "\"%s\"" (ts.ToString("o"))
    | None    -> "null"
  let lastErr =
    match s.lastError with
    | Some e -> JsonSerializer.Serialize e
    | None   -> "null"
  sprintf
    """{"lastScrapeAt":%s,"nextDueAt":"%s","lastSampleCount":%d,"lastDurationMs":%d,"lastError":%s}"""
    lastScrape (s.nextDueAt.ToString("o"))
    s.lastSampleCount s.lastDurationMs lastErr

let private targetJson (repo : IScrapeRepo) (t : ScrapeTarget) =
  let (TenantId tid) = t.tenantId
  let labelsJson =
    t.extraLabels
    |> Map.toSeq
    |> Seq.map (fun (k, v) ->
        sprintf "%s:%s" (JsonSerializer.Serialize k) (JsonSerializer.Serialize v))
    |> String.concat ","
    |> sprintf "{%s}"
  let auth =
    match t.bearerToken with
    | Some _ -> "true"
    | None   -> "false"
  let status =
    match repo.Status t.id with
    | Some s -> statusJson s
    | None   -> "null"
  sprintf
    """{"id":%s,"tenantId":%s,"url":%s,"intervalSec":%d,"labels":%s,"bearerAuth":%s,"createdAt":"%s","status":%s}"""
    (JsonSerializer.Serialize t.id)
    (JsonSerializer.Serialize tid)
    (JsonSerializer.Serialize t.url)
    t.intervalSec
    labelsJson auth
    (t.createdAt.ToString("o"))
    status

let private auditAdmin (log : IAuditLog) (ctx : HttpContext)
                       (action : string) (outcome : Outcome)
                       (details : string) =
  let t = PulseBoard.Rbac.tryGetTenant ctx
  let ev : AuditEvent =
    { ts       = DateTimeOffset.UtcNow
      tenant   = t |> Option.map (fun x -> x.tenant.id)
      apiKeyId = t |> Option.map (fun x -> x.apiKeyId)
      action   = action
      resource = ctx.request.path
      outcome  = outcome
      remoteIp = None
      details  = Some details }
  try log.Append ev with _ -> ()

let private listTargets (repo : IScrapeRepo) (store : ITenantStore)
                        (tenantId : string) : WebPart =
  fun ctx -> async {
    match store.TryGetTenant (TenantId tenantId) with
    | None -> return! errJson 404 "tenant not found" ctx
    | Some _ ->
      let body =
        repo.List (TenantId tenantId)
        |> Array.map (targetJson repo)
        |> String.concat ","
        |> sprintf "[%s]"
      return! jsonResp 200 body ctx
  }

let private createTarget (repo : IScrapeRepo) (store : ITenantStore)
                         (log : IAuditLog) (tenantId : string) : WebPart =
  fun ctx -> async {
    match store.TryGetTenant (TenantId tenantId) with
    | None ->
      auditAdmin log ctx "admin.scrape.create" Deny
        (sprintf "tenantId=%s not found" tenantId)
      return! errJson 404 "tenant not found" ctx
    | Some _ ->
      let body = readBody ctx.request
      match tryParseJson body with
      | None ->
        auditAdmin log ctx "admin.scrape.create" Deny "invalid json"
        return! errJson 400 "invalid JSON body" ctx
      | Some doc ->
        use _ = doc
        let root = doc.RootElement
        match tryGetString root "url" with
        | None ->
          auditAdmin log ctx "admin.scrape.create" Deny "missing url"
          return! errJson 400 "field 'url' is required" ctx
        | Some url ->
          let okScheme =
            url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
          if not okScheme then
            auditAdmin log ctx "admin.scrape.create" Deny
              (sprintf "bad url=%s" url)
            return! errJson 400 "url must be http:// or https://" ctx
          else
          let interval =
            tryGetInt root "intervalSec" |> Option.defaultValue 15
          if interval < 1 || interval > 86400 then
            auditAdmin log ctx "admin.scrape.create" Deny
              (sprintf "bad intervalSec=%d" interval)
            return! errJson 400 "intervalSec must be in [1, 86400]" ctx
          else
          let labels =
            match root.TryGetProperty "labels" with
            | true, el when el.ValueKind = JsonValueKind.Object ->
              el.EnumerateObject()
              |> Seq.choose (fun p ->
                  if p.Value.ValueKind = JsonValueKind.String then
                    Some (p.Name, p.Value.GetString())
                  else None)
              |> Map.ofSeq
            | _ -> Map.empty
          let bearer = tryGetString root "bearerToken"
          let id = Guid.NewGuid().ToString("N").Substring(0, 16)
          let t : ScrapeTarget =
            { id = id
              tenantId = TenantId tenantId
              url = url
              intervalSec = interval
              extraLabels = labels
              bearerToken = bearer
              createdAt = DateTimeOffset.UtcNow }
          repo.Upsert t
          auditAdmin log ctx "admin.scrape.create" Allow
            (sprintf "tenantId=%s id=%s url=%s intervalSec=%d labels=%d"
               tenantId id url interval labels.Count)
          return! jsonResp 201 (targetJson repo t) ctx
  }

let private deleteTarget (repo : IScrapeRepo) (log : IAuditLog)
                         (id : string) : WebPart =
  fun ctx -> async {
    match repo.TryGet id with
    | None ->
      auditAdmin log ctx "admin.scrape.delete" Deny
        (sprintf "id=%s not found" id)
      return! errJson 404 "scrape target not found" ctx
    | Some _ ->
      let _ = repo.Delete id
      auditAdmin log ctx "admin.scrape.delete" Allow
        (sprintf "id=%s" id)
      return! jsonResp 204 "" ctx
  }

let private getTarget (repo : IScrapeRepo) (id : string) : WebPart =
  fun ctx -> async {
    match repo.TryGet id with
    | None -> return! errJson 404 "scrape target not found" ctx
    | Some t -> return! jsonResp 200 (targetJson repo t) ctx
  }

/// Admin endpoints for scrape-target CRUD. Gating (Admin scope) is
/// applied by the caller alongside the rest of the admin surface.
let adminWebPart (repo : IScrapeRepo) (store : ITenantStore)
                 (log : IAuditLog) : WebPart =
  choose [
    GET    >=> pathScan "/api/admin/tenants/%s/scrape-targets"
                                                 (listTargets repo store)
    POST   >=> pathScan "/api/admin/tenants/%s/scrape-targets"
                                                 (createTarget repo store log)
    GET    >=> pathScan "/api/admin/scrape-targets/%s" (getTarget repo)
    DELETE >=> pathScan "/api/admin/scrape-targets/%s" (deleteTarget repo log)
  ]

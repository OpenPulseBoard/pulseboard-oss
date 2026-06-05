module PulseBoard.Synthetics

// PLAN-NEXT 14.8 — Synthetic & uptime checks.
//
// Small `http` / `tcp` / `dns` probes run from this edge on a fixed cadence.
// Tenants define targets in the portal; every probe result lands in the same
// pipeline as external telemetry:
//
//   * Metrics. `pulse_synthetic_up{check,kind,region}` (1 = up, 0 = down) and
//     `pulse_synthetic_duration_seconds{check,kind,region}` are appended to the
//     in-process MetricStore, so they are queryable and alertable with the
//     existing rule engine (alert on `pulse_synthetic_up == 0`).
//   * Logs. One line per probe into the `synthetics` service stream, so the
//     reason a probe failed (timeout / connection refused / wrong status) is
//     visible in Explore.
//
// The portal gets a CRUD surface plus a multi-region matrix view
// ("up from us-east, down from eu-west"). On a single OSS edge the matrix has
// one region; the cloud runs an edge per region writing into a shared store.
//
// SSRF guard: in multi-tenant (SaaS) mode the probe refuses to open a
// connection to a loopback / private / link-local address so a tenant cannot
// use a check to reach internal services or cloud metadata endpoints. A
// self-hosted single-tenant deployment allows private targets by default (you
// may legitimately want to probe `localhost:5432`); override either way with
// `--synthetic-allow-private=`.

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Diagnostics
open System.Collections.Concurrent
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open PulseBoard.Tenancy
open PulseBoard.TimeSeries

let private nowMs () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

// -- model ------------------------------------------------------------------

type ProbeKind =
  | Http
  | Tcp
  | Dns

let kindToStr = function Http -> "http" | Tcp -> "tcp" | Dns -> "dns"

let kindOfStr (s : string) =
  match (if isNull s then "" else s.Trim().ToLowerInvariant()) with
  | "http" | "https" -> Some Http
  | "tcp"            -> Some Tcp
  | "dns"            -> Some Dns
  | _                -> None

[<NoComparison>]
type Check =
  { id           : string
    name         : string
    kind         : ProbeKind
    target       : string      // http: URL · tcp: host:port · dns: hostname
    intervalMs   : int64
    timeoutMs    : int64
    expectStatus : int         // http only; 0 => any 2xx/3xx is "up"
    enabled      : bool
    createdAt    : int64
    updatedAt    : int64 }

[<NoComparison>]
type ProbeResult =
  { checkId    : string
    name       : string
    kind       : ProbeKind
    region     : string
    up         : bool
    durationMs : float
    detail     : string
    at         : int64 }

// Clamp user-supplied cadence/timeout into sane bounds so a typo can't peg a
// CPU (interval too low) or hang a worker (timeout too high).
let private minIntervalMs = 5_000L
let private minTimeoutMs  = 1_000L
let private maxTimeoutMs  = 60_000L

let normalise (c : Check) : Check =
  { c with
      intervalMs = max minIntervalMs c.intervalMs
      timeoutMs  = min maxTimeoutMs (max minTimeoutMs c.timeoutMs) }

/// Validate the user-supplied shape of a check; returns the normalised check
/// or a human-readable error.
let validate (c : Check) : Result<Check, string> =
  if String.IsNullOrWhiteSpace c.name then Result.Error "name is required"
  elif String.IsNullOrWhiteSpace c.target then Result.Error "target is required"
  else
    match c.kind with
    | Http ->
      match Uri.TryCreate(c.target, UriKind.Absolute) with
      | true, u when (u.Scheme = Uri.UriSchemeHttp || u.Scheme = Uri.UriSchemeHttps) ->
        Result.Ok (normalise c)
      | _ -> Result.Error "http target must be an absolute http(s) URL"
    | Tcp ->
      match c.target.Split(':') with
      | [| h; p |] when h.Length > 0 ->
        match Int32.TryParse p with
        | true, n when n > 0 && n <= 65535 -> Result.Ok (normalise c)
        | _ -> Result.Error "tcp target port must be 1-65535"
      | _ -> Result.Error "tcp target must be host:port"
    | Dns ->
      if c.target.Contains "/" then Result.Error "dns target must be a bare hostname"
      else Result.Ok (normalise c)

// -- JSON codec -------------------------------------------------------------

let private writeCheck (w : Utf8JsonWriter) (c : Check) =
  w.WriteStartObject()
  w.WriteString("id",            c.id)
  w.WriteString("name",          c.name)
  w.WriteString("kind",          kindToStr c.kind)
  w.WriteString("target",        c.target)
  w.WriteNumber("intervalMs",    c.intervalMs)
  w.WriteNumber("timeoutMs",     c.timeoutMs)
  w.WriteNumber("expectStatus",  c.expectStatus)
  w.WriteBoolean("enabled",      c.enabled)
  w.WriteNumber("createdAt",     c.createdAt)
  w.WriteNumber("updatedAt",     c.updatedAt)
  w.WriteEndObject()

let serialiseCheck (c : Check) : string =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    writeCheck w c)
  Encoding.UTF8.GetString(ms.ToArray())

let private readStr (el : JsonElement) (name : string) =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.String -> Some (v.GetString())
  | _ -> None

let private readInt64 (el : JsonElement) (name : string) (dflt : int64) =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.Number ->
    let mutable n = 0L in (if v.TryGetInt64 &n then n else dflt)
  | _ -> dflt

let private readBool (el : JsonElement) (name : string) (dflt : bool) =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.True  -> true
  | true, v when v.ValueKind = JsonValueKind.False -> false
  | _ -> dflt

let parseCheck (line : string) : Check option =
  try
    use doc = JsonDocument.Parse line
    let r = doc.RootElement
    match readStr r "id", kindOfStr (readStr r "kind" |> Option.defaultValue "") with
    | Some id, Some kind when id.Length > 0 ->
      Some {
        id           = id
        name         = readStr r "name"   |> Option.defaultValue ""
        kind         = kind
        target       = readStr r "target" |> Option.defaultValue ""
        intervalMs   = readInt64 r "intervalMs" 60_000L
        timeoutMs    = readInt64 r "timeoutMs"  5_000L
        expectStatus = int (readInt64 r "expectStatus" 0L)
        enabled      = readBool r "enabled" true
        createdAt    = readInt64 r "createdAt" 0L
        updatedAt    = readInt64 r "updatedAt" 0L }
    | _ -> None
  with _ -> None

/// Parse an inbound portal request body into a check, assigning an id and
/// timestamps. When `existing` is supplied (PUT-style upsert) its id and
/// `createdAt` are preserved.
let parseRequest (existing : Check option) (body : string) : Result<Check, string> =
  try
    use doc = JsonDocument.Parse body
    let r = doc.RootElement
    match kindOfStr (readStr r "kind" |> Option.defaultValue "") with
    | None -> Result.Error "kind must be one of: http, tcp, dns"
    | Some kind ->
      let now = nowMs ()
      let c =
        { id           = existing |> Option.map (fun e -> e.id)
                                   |> Option.defaultWith (fun () -> Guid.NewGuid().ToString "N")
          name         = readStr r "name"   |> Option.defaultValue ""
          kind         = kind
          target       = readStr r "target" |> Option.defaultValue ""
          intervalMs   = readInt64 r "intervalMs" 60_000L
          timeoutMs    = readInt64 r "timeoutMs"  5_000L
          expectStatus = int (readInt64 r "expectStatus" 0L)
          enabled      = readBool r "enabled" true
          createdAt    = existing |> Option.map (fun e -> e.createdAt)
                                  |> Option.defaultValue now
          updatedAt    = now }
      validate c
  with ex -> Result.Error ("invalid body: " + ex.Message)

let private writeResult (w : Utf8JsonWriter) (r : ProbeResult) =
  w.WriteStartObject()
  w.WriteBoolean("up",         r.up)
  w.WriteNumber("durationMs",  r.durationMs)
  w.WriteString("detail",      r.detail)
  w.WriteNumber("at",          r.at)
  w.WriteString("region",      r.region)
  w.WriteEndObject()

let serialiseResult (r : ProbeResult) : string =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    writeResult w r)
  Encoding.UTF8.GetString(ms.ToArray())

// -- store ------------------------------------------------------------------

type ISyntheticStore =
  abstract List   : TenantId -> Check[]
  abstract TryGet : TenantId * string -> Check option
  abstract Upsert : TenantId * Check -> unit
  abstract Delete : TenantId * string -> bool

let private sanitize (s : string) =
  let invalid = Path.GetInvalidFileNameChars()
  String(s.ToCharArray() |> Array.map (fun c -> if Array.contains c invalid then '_' else c))

type FileSyntheticStore(root : string) =
  do Directory.CreateDirectory root |> ignore
  let cache = ConcurrentDictionary<string, ConcurrentDictionary<string, Check>>()
  let sync  = obj ()
  let tenantDir (TenantId t) =
    let d = Path.Combine(root, sanitize t)
    Directory.CreateDirectory d |> ignore
    d
  let bucket (TenantId t as tid) =
    cache.GetOrAdd(t, fun _ ->
      let m = ConcurrentDictionary<string, Check>()
      let dir = tenantDir tid
      if Directory.Exists dir then
        for f in Directory.EnumerateFiles(dir, "*.json") do
          try
            match parseCheck (File.ReadAllText f) with
            | Some c -> m.[c.id] <- c
            | None -> ()
          with _ -> ()
      m)
  interface ISyntheticStore with
    member _.List tid =
      (bucket tid).Values |> Seq.sortBy (fun c -> c.name) |> Seq.toArray
    member _.TryGet(tid, id) =
      match (bucket tid).TryGetValue id with
      | true, c -> Some c
      | _ -> None
    member _.Upsert(tid, c) =
      lock sync (fun () ->
        (bucket tid).[c.id] <- c
        let path = Path.Combine(tenantDir tid, sanitize c.id + ".json")
        let tmp = path + ".tmp"
        File.WriteAllText(tmp, serialiseCheck c)
        if File.Exists path then File.Delete path
        File.Move(tmp, path))
    member _.Delete(tid, id) =
      lock sync (fun () ->
        let removed = (bucket tid).TryRemove id |> fst
        let path = Path.Combine(tenantDir tid, sanitize id + ".json")
        if File.Exists path then (try File.Delete path with _ -> ())
        removed)

// -- SSRF guard -------------------------------------------------------------

let private isPrivateIp (ip : IPAddress) : bool =
  if IPAddress.IsLoopback ip then true
  else
    match ip.AddressFamily with
    | AddressFamily.InterNetwork ->
      let b = ip.GetAddressBytes()
      b.[0] = 10uy
      || (b.[0] = 172uy && b.[1] >= 16uy && b.[1] <= 31uy)
      || (b.[0] = 192uy && b.[1] = 168uy)
      || (b.[0] = 169uy && b.[1] = 254uy)   // link-local / cloud metadata
      || b.[0] = 127uy
      || b.[0] = 0uy
      || (b.[0] = 100uy && b.[1] >= 64uy && b.[1] <= 127uy)  // CGNAT 100.64/10
    | AddressFamily.InterNetworkV6 ->
      ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal
      || (let b = ip.GetAddressBytes() in (b.[0] &&& 0xfeuy) = 0xfcuy)  // ULA fc00::/7
    | _ -> true

/// True when a connection to `host` is permitted. Resolves names so a
/// hostname pointing at a private address is also blocked. Fails closed.
let hostAllowed (allowPrivate : bool) (host : string) : bool =
  if allowPrivate then true
  elif String.IsNullOrWhiteSpace host then false
  else
    match IPAddress.TryParse host with
    | true, ip -> not (isPrivateIp ip)
    | _ ->
      try
        let ips = Dns.GetHostAddresses host
        ips.Length > 0 && ips |> Array.forall (fun ip -> not (isPrivateIp ip))
      with _ -> false

let private targetHost (c : Check) : string =
  match c.kind with
  | Http -> (try Uri(c.target).Host with _ -> "")
  | Tcp  -> (match c.target.Split(':') with a when a.Length >= 1 -> a.[0] | _ -> c.target)
  | Dns  -> c.target

// -- probes -----------------------------------------------------------------

// One shared client: pooled connections, no per-probe socket churn. Per-call
// timeout is enforced with a CancellationTokenSource, not HttpClient.Timeout,
// so concurrent probes don't share a single deadline.
let private http =
  let h = new HttpClient()
  h.Timeout <- Timeout.InfiniteTimeSpan
  h.DefaultRequestHeaders.UserAgent.ParseAdd "PulseBoard-Synthetics/1.0"
  h

let runHttp (target : string) (timeoutMs : int64) (expectStatus : int)
            : Async<bool * float * string> =
  async {
    let sw = Stopwatch.StartNew()
    try
      use cts = new CancellationTokenSource(int timeoutMs)
      use req = new HttpRequestMessage(HttpMethod.Get, target)
      let! resp =
        http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
        |> Async.AwaitTask
      sw.Stop()
      let code = int resp.StatusCode
      let ok =
        if expectStatus > 0 then code = expectStatus
        else code >= 200 && code < 400
      return ok, sw.Elapsed.TotalMilliseconds, sprintf "HTTP %d" code
    with
    | :? OperationCanceledException ->
      sw.Stop(); return false, sw.Elapsed.TotalMilliseconds, "timeout"
    | ex ->
      sw.Stop(); return false, sw.Elapsed.TotalMilliseconds, ex.GetType().Name + ": " + ex.Message
  }

let runTcp (target : string) (timeoutMs : int64) : Async<bool * float * string> =
  async {
    let sw = Stopwatch.StartNew()
    let host, port =
      match target.Split(':') with
      | [| h; p |] -> h, (match Int32.TryParse p with | true, n -> n | _ -> -1)
      | _ -> target, -1
    if port < 0 || port > 65535 then
      return false, 0.0, "invalid host:port"
    else
      try
        use client = new TcpClient()
        use cts = new CancellationTokenSource(int timeoutMs)
        do! client.ConnectAsync(host, port, cts.Token).AsTask() |> Async.AwaitTask
        sw.Stop()
        return true, sw.Elapsed.TotalMilliseconds, sprintf "connected %s:%d" host port
      with
      | :? OperationCanceledException ->
        sw.Stop(); return false, sw.Elapsed.TotalMilliseconds, "timeout"
      | ex ->
        sw.Stop(); return false, sw.Elapsed.TotalMilliseconds, ex.GetType().Name + ": " + ex.Message
  }

let runDns (target : string) (timeoutMs : int64) : Async<bool * float * string> =
  async {
    let sw = Stopwatch.StartNew()
    try
      use cts = new CancellationTokenSource(int timeoutMs)
      let! ips = Dns.GetHostAddressesAsync(target, cts.Token) |> Async.AwaitTask
      sw.Stop()
      if ips.Length > 0 then
        let detail = ips |> Array.map (fun i -> i.ToString()) |> String.concat ","
        return true, sw.Elapsed.TotalMilliseconds, detail
      else
        return false, sw.Elapsed.TotalMilliseconds, "no records"
    with
    | :? OperationCanceledException ->
      sw.Stop(); return false, sw.Elapsed.TotalMilliseconds, "timeout"
    | ex ->
      sw.Stop(); return false, sw.Elapsed.TotalMilliseconds, ex.GetType().Name + ": " + ex.Message
  }

// -- runner -----------------------------------------------------------------

let private esc (v : string) =
  v.Replace("\\", "\\\\").Replace("\"", "\\\"")

/// Encode a metric with the check labels inline as a Prometheus series string
/// (the MetricStore keys series by this full string).
let series (metric : string) (r : ProbeResult) =
  sprintf "%s{check=\"%s\",kind=\"%s\",region=\"%s\"}"
    metric (esc r.name) (kindToStr r.kind) (esc r.region)

/// Periodically evaluates every enabled check for every tenant and feeds the
/// results back into the metric + log stores. Slow probes are dispatched on
/// their own async so one hung target can't delay the scheduler tick.
type Runner(store        : ISyntheticStore,
            metrics      : MetricStore,
            logs         : LogStore,
            region       : string,
            allowPrivate : bool) =
  let cts = new CancellationTokenSource()
  let mutable tenantsProvider : unit -> TenantId[] = fun () -> [||]
  // last scheduler-fire time and last result, keyed by "<tenant>\0<checkId>".
  let lastRun    = ConcurrentDictionary<string, int64>()
  let lastResult = ConcurrentDictionary<string, ProbeResult>()
  let key (TenantId t) (id : string) = t + "\u0000" + id

  let record (r : ProbeResult) =
    try
      metrics.Record(series "pulse_synthetic_up" r,
                     { ts = r.at; value = (if r.up then 1.0 else 0.0) })
      metrics.Record(series "pulse_synthetic_duration_seconds" r,
                     { ts = r.at; value = r.durationMs / 1000.0 })
      logs.Add({ ts      = r.at
                 service = "synthetics"
                 level   = (if r.up then "info" else "error")
                 message =
                   sprintf "%s check '%s' %s in %.0fms — %s"
                     (kindToStr r.kind) r.name
                     (if r.up then "UP" else "DOWN") r.durationMs r.detail })
    with _ -> ()

  /// Run a single probe now (no scheduling, no persistence of the schedule
  /// clock). Applies the SSRF guard and records metrics + a log line.
  member _.Probe (c : Check) : Async<ProbeResult> =
    async {
      let at = nowMs ()
      let blocked =
        (c.kind = Http || c.kind = Tcp)
        && not (hostAllowed allowPrivate (targetHost c))
      let! up, dur, detail =
        if blocked then
          async { return false, 0.0, "blocked: private/loopback target not allowed" }
        else
          match c.kind with
          | Http -> runHttp c.target c.timeoutMs c.expectStatus
          | Tcp  -> runTcp  c.target c.timeoutMs
          | Dns  -> runDns  c.target c.timeoutMs
      let r =
        { checkId = c.id; name = c.name; kind = c.kind; region = region
          up = up; durationMs = dur; detail = detail; at = at }
      record r
      return r
    }

  /// On-demand run for the portal "Run now" button: probes, records, and
  /// stores the result so it appears in the matrix immediately.
  member this.RunNow (tid : TenantId) (c : Check) : Async<ProbeResult> =
    async {
      let! r = this.Probe c
      lastResult.[key tid c.id] <- r
      return r
    }

  /// Latest result per check id for a tenant (for the matrix view).
  member _.LastResults (tid : TenantId) : Map<string, ProbeResult> =
    store.List tid
    |> Array.choose (fun c ->
      match lastResult.TryGetValue (key tid c.id) with
      | true, r -> Some (c.id, r)
      | _ -> None)
    |> Map.ofArray

  member _.Region = region

  member _.SetTenantsProvider(f : unit -> TenantId[]) = tenantsProvider <- f

  member this.Start() =
    let worker =
      async {
        while not cts.Token.IsCancellationRequested do
          try
            let now = nowMs ()
            for tid in tenantsProvider () do
              for c in store.List tid do
                if c.enabled then
                  let k = key tid c.id
                  let due =
                    match lastRun.TryGetValue k with
                    | true, t -> now - t >= c.intervalMs
                    | _ -> true
                  if due then
                    lastRun.[k] <- now
                    Async.Start(
                      (async {
                        let! r = this.Probe c
                        lastResult.[k] <- r
                       }),
                      cts.Token)
          with _ -> ()
          do! Async.Sleep 1000
      }
    Async.Start(worker, cts.Token)

  member _.Stop() = try cts.Cancel() with _ -> ()

// -- REST -------------------------------------------------------------------

let private jsonResp (status : int) (body : string) : WebPart =
  let writer =
    match status with
    | 200 -> OK | 201 -> Suave.Successful.CREATED
    | 400 -> BAD_REQUEST | 404 -> NOT_FOUND
    | _   -> Suave.ServerErrors.INTERNAL_ERROR
  writer body >=> Writers.setMimeType "application/json"

let private errJson (status : int) (msg : string) =
  jsonResp status (sprintf """{"error":%s}""" (JsonSerializer.Serialize msg))

let private resolveTenant multiTenant (ctx : HttpContext) =
  if multiTenant then
    PulseBoard.Rbac.tryGetTenant ctx |> Option.map (fun t -> t.tenant.id)
  else Some (TenantId "__local__")

let private readBody (req : HttpRequest) =
  if isNull req.rawForm || req.rawForm.Length = 0 then ""
  else Encoding.UTF8.GetString req.rawForm

let private serialiseList (checks : Check[]) (results : Map<string, ProbeResult>) : string =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    w.WriteStartArray()
    for c in checks do
      w.WriteStartObject()
      w.WriteString("id",           c.id)
      w.WriteString("name",         c.name)
      w.WriteString("kind",         kindToStr c.kind)
      w.WriteString("target",       c.target)
      w.WriteNumber("intervalMs",   c.intervalMs)
      w.WriteNumber("timeoutMs",    c.timeoutMs)
      w.WriteNumber("expectStatus", c.expectStatus)
      w.WriteBoolean("enabled",     c.enabled)
      w.WriteNumber("createdAt",    c.createdAt)
      w.WriteNumber("updatedAt",    c.updatedAt)
      w.WritePropertyName "lastResult"
      match results.TryFind c.id with
      | Some r -> writeResult w r
      | None   -> w.WriteNullValue()
      w.WriteEndObject()
    w.WriteEndArray())
  Encoding.UTF8.GetString(ms.ToArray())

let private serialiseMatrix (region : string) (checks : Check[])
                            (results : Map<string, ProbeResult>) : string =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    w.WriteStartObject()
    w.WritePropertyName "regions"
    w.WriteStartArray()
    w.WriteStringValue region
    w.WriteEndArray()
    w.WritePropertyName "checks"
    w.WriteStartArray()
    for c in checks do
      w.WriteStartObject()
      w.WriteString("id",      c.id)
      w.WriteString("name",    c.name)
      w.WriteString("kind",    kindToStr c.kind)
      w.WriteString("target",  c.target)
      w.WriteBoolean("enabled", c.enabled)
      w.WritePropertyName "results"
      w.WriteStartObject()
      match results.TryFind c.id with
      | Some r ->
        w.WritePropertyName region
        writeResult w r
      | None -> ()
      w.WriteEndObject()
      w.WriteEndObject()
    w.WriteEndArray()
    w.WriteEndObject())
  Encoding.UTF8.GetString(ms.ToArray())

let webPart (multiTenant     : bool)
            (store           : ISyntheticStore)
            (lastResultsOf   : TenantId -> Map<string, ProbeResult>)
            (probeNow        : TenantId -> Check -> Async<ProbeResult>)
            (region          : string) : WebPart =
  let withTenant (handler : TenantId -> WebPart) : WebPart =
    fun ctx -> async {
      match resolveTenant multiTenant ctx with
      | None -> return! errJson 401 "no tenant" ctx
      | Some tid -> return! handler tid ctx
    }

  choose [
    GET >=> path "/api/synthetics" >=>
      withTenant (fun tid ->
        jsonResp 200 (serialiseList (store.List tid) (lastResultsOf tid)))

    GET >=> path "/api/synthetics/matrix" >=>
      withTenant (fun tid ->
        jsonResp 200 (serialiseMatrix region (store.List tid) (lastResultsOf tid)))

    POST >=> path "/api/synthetics" >=>
      withTenant (fun tid ->
        fun ctx -> async {
          match parseRequest None (readBody ctx.request) with
          | Result.Error msg -> return! errJson 400 msg ctx
          | Result.Ok c ->
            store.Upsert(tid, c)
            return! jsonResp 201 (serialiseCheck c) ctx
        })

    POST >=> pathScan "/api/synthetics/%s/run" (fun id ->
      withTenant (fun tid ->
        fun ctx -> async {
          match store.TryGet(tid, id) with
          | None -> return! errJson 404 "no such check" ctx
          | Some c ->
            let! r = probeNow tid c
            return! jsonResp 200 (serialiseResult r) ctx
        }))

    PUT >=> pathScan "/api/synthetics/%s" (fun id ->
      withTenant (fun tid ->
        fun ctx -> async {
          let existing = store.TryGet(tid, id)
          match existing with
          | None -> return! errJson 404 "no such check" ctx
          | Some _ ->
            match parseRequest existing (readBody ctx.request) with
            | Result.Error msg -> return! errJson 400 msg ctx
            | Result.Ok c ->
              store.Upsert(tid, c)
              return! jsonResp 200 (serialiseCheck c) ctx
        }))

    GET >=> pathScan "/api/synthetics/%s" (fun id ->
      withTenant (fun tid ->
        match store.TryGet(tid, id) with
        | Some c -> jsonResp 200 (serialiseCheck c)
        | None   -> errJson 404 "no such check"))

    DELETE >=> pathScan "/api/synthetics/%s" (fun id ->
      withTenant (fun tid ->
        if store.Delete(tid, id) then jsonResp 200 """{"deleted":true}"""
        else errJson 404 "no such check"))
  ]

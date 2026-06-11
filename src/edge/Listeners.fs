module PulseBoard.Listeners

open System
open System.Collections.Concurrent
open System.Globalization
open System.Net
open System.Net.Sockets
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

// StatsD UDP + Carbon plaintext TCP. Both are
// auth-less wire protocols by tradition; we attribute traffic to a
// tenant by listener — each registered listener owns one TCP/UDP port,
// and every line received on that port is charged to that tenant's
// cardinality budget and written through the same MetricStore as every
// other receiver.
//
// Wire formats:
//   StatsD (DogStatsD dialect):
//     name:value|type[|@samplerate][|#k:v,k:v,...]
//     types: c (counter)  g (gauge)  ms|h|d (timer/histogram/distribution
//            — treated as gauge samples for now)  s (set — treated as gauge)
//     multiple metrics per packet separated by '\n'
//
//   Carbon plaintext:
//     metric.path value unix_seconds\n
//     no labels; dot-namespaced names land verbatim
//
// Out-of-scope (deliberate): StatsD over TCP, Carbon pickle protocol,
// Graphite tag syntax (`name;tag=val`). Easy to add later.

// -- model -------------------------------------------------------------------

type ListenerProtocol = Statsd | Carbon

let protocolStr = function
  | Statsd -> "statsd"
  | Carbon -> "carbon"

let tryParseProtocol (s : string) =
  match s.Trim().ToLowerInvariant() with
  | "statsd" -> Some Statsd
  | "carbon" -> Some Carbon
  | _ -> None

type Listener =
  { id        : string
    tenantId  : TenantId
    protocol  : ListenerProtocol
    port      : int
    /// Optional address override; defaults to 127.0.0.1 for safety in
    /// dev. Production deployments override with 0.0.0.0 via the API.
    bindAddr  : string
    createdAt : DateTimeOffset }

[<NoComparison; NoEquality>]
type ListenerStatus =
  { running       : bool
    boundEndpoint : string option
    linesReceived : int64
    samplesAccepted : int64
    rejectedCardinality : int64
    lastError     : string option
    lastActivityAt : DateTimeOffset option }

let private emptyStatus : ListenerStatus =
  { running = false; boundEndpoint = None
    linesReceived = 0L; samplesAccepted = 0L
    rejectedCardinality = 0L
    lastError = None; lastActivityAt = None }

// -- canonical naming (mirrors PromScrape / PromRemoteWrite / OTLP / LokiPush) -

[<Struct>] type private Label = { name : string; value : string }

let private canonicalName (metric : string) (labels : Label[]) : string =
  if labels.Length = 0 then metric
  else
    let sorted = Array.copy labels
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

// -- repo --------------------------------------------------------------------

type IListenerRepo =
  abstract member List      : TenantId -> Listener array
  abstract member ListAll   : unit -> Listener array
  abstract member TryGet    : string -> Listener option
  abstract member ByPort    : ListenerProtocol * int -> Listener option
  abstract member Upsert    : Listener -> unit
  abstract member Delete    : string -> bool
  abstract member Status    : string -> ListenerStatus option
  abstract member SetStatus : string * ListenerStatus -> unit

type InMemoryListenerRepo() =
  let items = ConcurrentDictionary<string, Listener>()
  let statuses = ConcurrentDictionary<string, ListenerStatus>()
  interface IListenerRepo with
    member _.List (tid : TenantId) =
      items.Values
      |> Seq.filter (fun l -> l.tenantId = tid)
      |> Seq.sortBy (fun l -> l.createdAt)
      |> Seq.toArray
    member _.ListAll () = items.Values |> Seq.toArray
    member _.TryGet id =
      match items.TryGetValue id with
      | true, l -> Some l | _ -> None
    member _.ByPort (proto, port) =
      items.Values
      |> Seq.tryFind (fun l -> l.protocol = proto && l.port = port)
    member _.Upsert (l : Listener) =
      items.[l.id] <- l
      statuses.TryAdd(l.id, emptyStatus) |> ignore
    member _.Delete (id : string) =
      let removed, _ = items.TryRemove id
      statuses.TryRemove id |> ignore
      removed
    member _.Status id =
      match statuses.TryGetValue id with
      | true, s -> Some s | _ -> None
    member _.SetStatus (id, s) = statuses.[id] <- s

// -- parsers -----------------------------------------------------------------

[<NoComparison; NoEquality>]
type private ParsedMetric =
  { metric : string
    value  : float
    /// Optional unix-ms timestamp; None means "now". Carbon supplies it,
    /// StatsD does not.
    tsMs   : int64 option
    labels : Label[] }

/// Parse a single DogStatsD-style line. Returns a list because a `ms` /
/// `h` / `d` line could be expanded into multiple percentile series in a
/// later pass — for now each line yields one sample. Returns [] on bad
/// input.
let private parseStatsdLine (line : string) : ParsedMetric list =
  let line = line.Trim()
  if line.Length = 0 then [] else
  // Split into segments by '|'
  let colon = line.IndexOf ':'
  if colon <= 0 then [] else
  let name = line.Substring(0, colon)
  let rest = line.Substring(colon + 1)
  let parts = rest.Split '|'
  if parts.Length < 2 then [] else
  let valStr = parts.[0]
  let typ    = parts.[1]
  // Parse value: floats; counters allow leading +/- to mean delta but
  // we record the literal value either way (the storage layer doesn't
  // distinguish counter vs gauge yet — both are timeseries of samples).
  let okV, baseVal =
    Double.TryParse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture)
  if not okV then [] else
  // sample rate (@0.1 means 1/10 sampled — extrapolate up)
  let mutable rate = 1.0
  let mutable tags : (string * string) list = []
  for i in 2 .. parts.Length - 1 do
    let p = parts.[i]
    if p.Length >= 2 && p.[0] = '@' then
      let ok, r =
        Double.TryParse(p.Substring 1, NumberStyles.Float, CultureInfo.InvariantCulture)
      if ok && r > 0.0 && r <= 1.0 then rate <- r
    elif p.Length >= 1 && p.[0] = '#' then
      for tagPart in p.Substring(1).Split ',' do
        if tagPart.Length > 0 then
          let eq = tagPart.IndexOf ':'
          if eq > 0 then
            tags <- (tagPart.Substring(0, eq), tagPart.Substring(eq + 1)) :: tags
          else
            tags <- (tagPart, "") :: tags
  let effective =
    match typ with
    | "c" | "d" -> baseVal / rate
    | _ -> baseVal
  let labels =
    tags
    |> List.rev
    |> List.map (fun (k, v) -> { name = k; value = v })
    |> List.toArray
  [ { metric = name; value = effective; tsMs = None; labels = labels } ]

/// Parse a single Carbon plaintext line: `name value ts_seconds`.
let private parseCarbonLine (line : string) : ParsedMetric option =
  let line = line.Trim()
  if line.Length = 0 then None else
  let parts = line.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
  if parts.Length < 2 then None else
  let okV, v =
    Double.TryParse(parts.[1], NumberStyles.Float, CultureInfo.InvariantCulture)
  if not okV then None else
  let tsMs =
    if parts.Length >= 3 then
      let okT, t =
        Int64.TryParse(parts.[2], NumberStyles.Integer, CultureInfo.InvariantCulture)
      if okT then Some (t * 1000L) else None
    else None
  Some { metric = parts.[0]; value = v; tsMs = tsMs; labels = [||] }

// -- ingest -------------------------------------------------------------------

[<NoComparison; NoEquality>]
type ListenerDeps =
  { repo    : IListenerRepo
    storage : IStorageClient
    quotas  : IngestQuotas option }

let private auditDeny (q : IngestQuotas) (listener : Listener)
                      (details : string) =
  let ev : AuditEvent =
    { ts       = DateTimeOffset.UtcNow
      tenant   = Some listener.tenantId
      apiKeyId = None
      action   = "listener.quota.cardinality"
      resource = sprintf "%s://%s:%d"
                   (protocolStr listener.protocol)
                   listener.bindAddr listener.port
      outcome  = Deny
      remoteIp = None
      details  = Some details }
  try q.auditLog.Append ev with _ -> ()

/// Apply one parsed metric: cardinality admission, Record + publish.
/// Returns (acceptedDelta, rejectedDelta).
let private ingestOne (deps : ListenerDeps) (l : Listener)
                      (m : ParsedMetric) : struct(int * int) =
  let name = canonicalName m.metric m.labels
  if name.Length = 0 || Double.IsNaN m.value then struct(0, 0)
  else
    let admit =
      match deps.quotas with
      | Some q ->
        match q.limiter.TryAdmitSeries(l.tenantId, name) with
        | CardinalityResult.Ok -> true
        | CardinalityResult.Rejected cap ->
          auditDeny q l (sprintf "series=%s cap=%d" name cap)
          false
      | None -> true
    if not admit then struct(0, 1)
    else
      let ts =
        match m.tsMs with
        | Some n -> n
        | None   -> DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
      // UDP/TCP listeners drive `ingestOne` per parsed line. We push
      // single-sample batches to the storage client; under the in-proc
      // implementation this is just MetricStore.Record + hub.Publish.
      // Under the HTTP edge variant the listener naturally backpressures
      // through the synchronous wait — that's the right behaviour for a
      // packet-driven receiver.
      let sample : MetricSample = { seriesName = name; tsMs = ts; value = m.value }
      let (TenantId tidStr) = l.tenantId
      deps.storage.WriteMetricSamples(tidStr, [| sample |])
      |> Async.RunSynchronously
      struct(1, 0)

let private bumpStatus (repo : IListenerRepo) (id : string)
                       (lines : int) (accepted : int) (rejected : int)
                       (err : string option) =
  let prev = repo.Status id |> Option.defaultValue emptyStatus
  let next =
    { prev with
        linesReceived       = prev.linesReceived + int64 lines
        samplesAccepted     = prev.samplesAccepted + int64 accepted
        rejectedCardinality = prev.rejectedCardinality + int64 rejected
        lastError = (match err with Some _ as e -> e | None -> prev.lastError)
        lastActivityAt      = Some DateTimeOffset.UtcNow }
  repo.SetStatus(id, next)

// -- listener processes -------------------------------------------------------

/// Track every running listener so they can be shut down individually
/// (on DELETE) or all at once (on process shutdown).
type private Running =
  { cts : CancellationTokenSource
    dispose : unit -> unit }

let private running = ConcurrentDictionary<string, Running>()

let private markRunning (repo : IListenerRepo) (id : string)
                        (ep : string) =
  let prev = repo.Status id |> Option.defaultValue emptyStatus
  repo.SetStatus(id,
    { prev with running = true; boundEndpoint = Some ep; lastError = None })

let private markStopped (repo : IListenerRepo) (id : string)
                        (err : string option) =
  let prev = repo.Status id |> Option.defaultValue emptyStatus
  repo.SetStatus(id,
    { prev with running = false
                lastError =
                  match err with Some _ as e -> e | None -> prev.lastError })

let private startStatsd (deps : ListenerDeps) (l : Listener) : Running =
  let cts = new CancellationTokenSource()
  let addr =
    try IPAddress.Parse l.bindAddr
    with _ -> IPAddress.Loopback
  let client = new UdpClient(IPEndPoint(addr, l.port))
  markRunning deps.repo l.id (sprintf "udp:%s:%d" l.bindAddr l.port)
  let loop = async {
    let token = cts.Token
    try
      while not token.IsCancellationRequested do
        let! recv =
          client.ReceiveAsync(token).AsTask() |> Async.AwaitTask
        let text = Encoding.UTF8.GetString recv.Buffer
        let lines = text.Split '\n'
        let mutable accepted = 0
        let mutable rejected = 0
        let mutable lineCount = 0
        for raw in lines do
          let line =
            if raw.EndsWith "\r" then raw.Substring(0, raw.Length - 1) else raw
          if line.Length > 0 then
            lineCount <- lineCount + 1
            for m in parseStatsdLine line do
              let struct(a, r) = ingestOne deps l m
              accepted <- accepted + a
              rejected <- rejected + r
        if lineCount > 0 then
          bumpStatus deps.repo l.id lineCount accepted rejected None
    with
    | :? OperationCanceledException -> ()
    | ex -> markStopped deps.repo l.id (Some ex.Message)
    try client.Dispose() with _ -> ()
    markStopped deps.repo l.id None
  }
  Async.Start(loop, cts.Token)
  { cts = cts; dispose = fun () -> try client.Dispose() with _ -> () }

let private handleCarbonClient (deps : ListenerDeps) (l : Listener)
                               (tcp : TcpClient) (token : CancellationToken) =
  async {
    try
      use tcp = tcp
      use stream = tcp.GetStream()
      use reader = new System.IO.StreamReader(stream, Encoding.UTF8)
      let mutable stop = false
      while not stop && not token.IsCancellationRequested do
        let! lineTask =
          reader.ReadLineAsync(token).AsTask() |> Async.AwaitTask
        if isNull lineTask then stop <- true
        else
          match parseCarbonLine lineTask with
          | None -> bumpStatus deps.repo l.id 1 0 0 None
          | Some m ->
            let struct(a, r) = ingestOne deps l m
            bumpStatus deps.repo l.id 1 a r None
    with
    | :? OperationCanceledException -> ()
    | _ -> ()
  }

let private startCarbon (deps : ListenerDeps) (l : Listener) : Running =
  let cts = new CancellationTokenSource()
  let addr =
    try IPAddress.Parse l.bindAddr
    with _ -> IPAddress.Loopback
  let server = new TcpListener(addr, l.port)
  server.Start()
  markRunning deps.repo l.id (sprintf "tcp:%s:%d" l.bindAddr l.port)
  let loop = async {
    let token = cts.Token
    try
      while not token.IsCancellationRequested do
        let! tcp = server.AcceptTcpClientAsync(token).AsTask() |> Async.AwaitTask
        Async.Start(handleCarbonClient deps l tcp token, token)
    with
    | :? OperationCanceledException -> ()
    | ex -> markStopped deps.repo l.id (Some ex.Message)
    try server.Stop() with _ -> ()
    markStopped deps.repo l.id None
  }
  Async.Start(loop, cts.Token)
  { cts = cts; dispose = fun () ->
      try server.Stop() with _ -> () }

let private startOne (deps : ListenerDeps) (l : Listener) =
  let r =
    match l.protocol with
    | Statsd -> startStatsd deps l
    | Carbon -> startCarbon deps l
  running.[l.id] <- r

let private stopOne (id : string) =
  match running.TryRemove id with
  | true, r ->
    try r.cts.Cancel() with _ -> ()
    try r.dispose() with _ -> ()
    try r.cts.Dispose() with _ -> ()
  | _ -> ()

// -- manager --------------------------------------------------------------

/// Lifecycle wrapper. `Start` boots all listeners currently in the repo
/// (used at process startup if any persisted), `Add` starts one
/// listener (used right after Upsert from the admin handler), `Remove`
/// stops one. `Dispose` shuts every listener down.
type Manager(deps : ListenerDeps) =
  member _.Deps = deps
  member _.StartAll () =
    for l in deps.repo.ListAll() do
      if not (running.ContainsKey l.id) then
        try startOne deps l
        with ex ->
          markStopped deps.repo l.id (Some ex.Message)
  member _.Add (l : Listener) =
    try startOne deps l
    with ex ->
      markStopped deps.repo l.id (Some ex.Message)
      reraise ()
  member _.Remove (id : string) = stopOne id
  interface IDisposable with
    member _.Dispose () =
      for id in running.Keys |> Seq.toArray do
        stopOne id

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
      | 409 -> Suave.RequestErrors.CONFLICT
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

let private statusJson (s : ListenerStatus) =
  let lastErr =
    match s.lastError with
    | Some e -> JsonSerializer.Serialize e
    | None   -> "null"
  let lastAct =
    match s.lastActivityAt with
    | Some t -> sprintf "\"%s\"" (t.ToString("o"))
    | None   -> "null"
  let bound =
    match s.boundEndpoint with
    | Some e -> JsonSerializer.Serialize e
    | None   -> "null"
  sprintf
    """{"running":%b,"boundEndpoint":%s,"linesReceived":%d,"samplesAccepted":%d,"rejectedCardinality":%d,"lastError":%s,"lastActivityAt":%s}"""
    s.running bound s.linesReceived s.samplesAccepted
    s.rejectedCardinality lastErr lastAct

let private listenerJson (repo : IListenerRepo) (l : Listener) =
  let (TenantId tid) = l.tenantId
  let status =
    match repo.Status l.id with
    | Some s -> statusJson s
    | None   -> "null"
  sprintf
    """{"id":%s,"tenantId":%s,"protocol":"%s","port":%d,"bindAddr":%s,"createdAt":"%s","status":%s}"""
    (JsonSerializer.Serialize l.id)
    (JsonSerializer.Serialize tid)
    (protocolStr l.protocol)
    l.port
    (JsonSerializer.Serialize l.bindAddr)
    (l.createdAt.ToString("o"))
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

let private listListeners (repo : IListenerRepo) (store : ITenantStore)
                          (tenantId : string) : WebPart =
  fun ctx -> async {
    match store.TryGetTenant (TenantId tenantId) with
    | None -> return! errJson 404 "tenant not found" ctx
    | Some _ ->
      let body =
        repo.List (TenantId tenantId)
        |> Array.map (listenerJson repo)
        |> String.concat ","
        |> sprintf "[%s]"
      return! jsonResp 200 body ctx
  }

let private createListener (mgr : Manager) (store : ITenantStore)
                           (log : IAuditLog) (tenantId : string) : WebPart =
  fun ctx -> async {
    let repo = mgr.Deps.repo
    match store.TryGetTenant (TenantId tenantId) with
    | None ->
      auditAdmin log ctx "admin.listener.create" Deny
        (sprintf "tenantId=%s not found" tenantId)
      return! errJson 404 "tenant not found" ctx
    | Some _ ->
      let body = readBody ctx.request
      match tryParseJson body with
      | None ->
        auditAdmin log ctx "admin.listener.create" Deny "invalid json"
        return! errJson 400 "invalid JSON body" ctx
      | Some doc ->
        use _ = doc
        let root = doc.RootElement
        let protoStr =
          tryGetString root "protocol" |> Option.defaultValue ""
        match tryParseProtocol protoStr with
        | None ->
          auditAdmin log ctx "admin.listener.create" Deny
            (sprintf "bad protocol=%s" protoStr)
          return! errJson 400
            "field 'protocol' must be 'statsd' or 'carbon'" ctx
        | Some proto ->
          match tryGetInt root "port" with
          | None ->
            auditAdmin log ctx "admin.listener.create" Deny "missing port"
            return! errJson 400 "field 'port' is required" ctx
          | Some port when port < 1 || port > 65535 ->
            auditAdmin log ctx "admin.listener.create" Deny
              (sprintf "bad port=%d" port)
            return! errJson 400 "port must be in [1, 65535]" ctx
          | Some port ->
            match repo.ByPort(proto, port) with
            | Some existing ->
              let (TenantId t2) = existing.tenantId
              auditAdmin log ctx "admin.listener.create" Deny
                (sprintf "port %d/%s already bound by tenant=%s id=%s"
                   port (protocolStr proto) t2 existing.id)
              return! errJson 409
                (sprintf "port %d already bound by another %s listener"
                   port (protocolStr proto)) ctx
            | None ->
              let bind =
                tryGetString root "bindAddr"
                |> Option.defaultValue "127.0.0.1"
              let id = Guid.NewGuid().ToString("N").Substring(0, 16)
              let l : Listener =
                { id = id
                  tenantId = TenantId tenantId
                  protocol = proto
                  port = port
                  bindAddr = bind
                  createdAt = DateTimeOffset.UtcNow }
              try
                repo.Upsert l
                mgr.Add l
                auditAdmin log ctx "admin.listener.create" Allow
                  (sprintf "tenantId=%s id=%s %s://%s:%d"
                     tenantId id (protocolStr proto) bind port)
                return! jsonResp 201 (listenerJson repo l) ctx
              with ex ->
                // Bind failed (address in use, permission, etc); roll back.
                repo.Delete id |> ignore
                auditAdmin log ctx "admin.listener.create" Error
                  (sprintf "bind failed: %s" ex.Message)
                return! errJson 400
                  (sprintf "failed to bind %s://%s:%d — %s"
                     (protocolStr proto) bind port ex.Message) ctx
  }

let private deleteListener (mgr : Manager) (log : IAuditLog)
                           (id : string) : WebPart =
  fun ctx -> async {
    let repo = mgr.Deps.repo
    match repo.TryGet id with
    | None ->
      auditAdmin log ctx "admin.listener.delete" Deny
        (sprintf "id=%s not found" id)
      return! errJson 404 "listener not found" ctx
    | Some _ ->
      mgr.Remove id
      let _ = repo.Delete id
      auditAdmin log ctx "admin.listener.delete" Allow
        (sprintf "id=%s" id)
      return! jsonResp 204 "" ctx
  }

let private getListener (repo : IListenerRepo) (id : string) : WebPart =
  fun ctx -> async {
    match repo.TryGet id with
    | None   -> return! errJson 404 "listener not found" ctx
    | Some l -> return! jsonResp 200 (listenerJson repo l) ctx
  }

let adminWebPart (mgr : Manager) (store : ITenantStore)
                 (log : IAuditLog) : WebPart =
  let repo = mgr.Deps.repo
  choose [
    GET    >=> pathScan "/api/admin/tenants/%s/listeners"
                                                 (listListeners repo store)
    POST   >=> pathScan "/api/admin/tenants/%s/listeners"
                                                 (createListener mgr store log)
    GET    >=> pathScan "/api/admin/listeners/%s" (getListener repo)
    DELETE >=> pathScan "/api/admin/listeners/%s" (deleteListener mgr log)
  ]

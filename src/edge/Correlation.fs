module PulseBoard.Correlation

// End-to-end correlation by default.
//
// PulseBoard already keeps metrics, logs and traces in one process. This
// module wires them together so an operator never has to copy a service
// name and a timestamp between three tabs by hand:
//
//   1.  `deriveService` pulls the most likely service identifier out of an
//       alert's (or a metric series') label-set — `service`, then the
//       common fallbacks (`service_name`, `job`, `app`, `container`, …).
//
//   2.  `correlate` takes a `(service, fromMs, toMs)` window and returns the
//       most relevant logs (errors first, newest first) plus the single
//       slowest trace touching that service in the window. This is what the
//       UI's "show logs for this spike" right-click and the alert detail
//       view consume.
//
//   3.  `Snapshotter` observes the alert sink — exactly like the runbook
//       `Tracker` — and the moment an alert starts firing it captures a
//       correlation snapshot into a bounded per-tenant cache keyed by
//       fingerprint. Notifications and the portal read that cached snapshot
//       so the "top 3 log lines + slowest trace from the breach window" are
//       frozen at fire time, not recomputed (and possibly empty) minutes
//       later when someone opens the alert.
//
//   4.  `exemplarsFor` derives trace exemplars for a metric/service window
//       straight off the span store, so histogram panels can surface
//       exemplars with no separate exemplar-ingestion path or opt-in config.
//
// Everything is in-memory and bounded, matching the embedded
// `MetricStore` / `LogStore` / `InMemorySpanStore` it reads from. When the
// process restarts the cache is empty; recurring alerts repopulate it on
// their next fire.

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open PulseBoard.Tenancy
open PulseBoard.TimeSeries
open PulseBoard.Spans
open PulseBoard.Rules

let private nowMs () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

// -- service derivation -----------------------------------------------------

/// Label keys we treat as carrying a service identity, in priority order.
/// `service` wins (OTel resource attribute `service.name` lands here after
/// relabeling); the rest are common Prometheus / log conventions.
let private serviceLabelKeys =
  [ "service"; "service_name"; "service.name"; "job"; "app"
    "application"; "container"; "deployment"; "pod" ]

/// Best-effort extraction of a service name from a label map. Returns
/// `None` when no recognised key carries a non-empty value — callers then
/// fall back to an un-scoped (all-services) correlation.
let deriveService (labels : Map<string,string>) : string option =
  serviceLabelKeys
  |> List.tryPick (fun k ->
    match labels.TryFind k with
    | Some v when not (String.IsNullOrWhiteSpace v) -> Some v
    | _ -> None)

// -- model ------------------------------------------------------------------

[<NoComparison>]
type CorrelatedTrace =
  { summary : TraceSummary
    spans   : Span array }

[<NoComparison>]
type CorrelationSnapshot =
  { service      : string option   // None = not scoped to a single service
    fromMs       : int64
    toMs         : int64
    logs         : LogEntry array   // already trimmed + ranked
    slowestTrace : CorrelatedTrace option
    generatedAt  : int64 }

// -- correlation core -------------------------------------------------------

/// Rank candidate log lines for a window: errors/warnings first, then most
/// recent. `limit` caps the result (the UI shows the top few; notifications
/// embed the top 3).
let private rankLogs (limit : int) (entries : LogEntry array) : LogEntry array =
  let severityRank (lvl : string) =
    match (if isNull lvl then "" else lvl.ToLowerInvariant()) with
    | "fatal" | "critical" | "crit" -> 0
    | "error" | "err"               -> 1
    | "warn"  | "warning"           -> 2
    | _                             -> 3
  entries
  |> Array.sortBy (fun e -> severityRank e.level, - e.ts)
  |> fun xs -> if xs.Length <= limit then xs else xs.[.. limit - 1]

/// True when a log entry plausibly belongs to `service`. We match on the
/// log's `service` field case-insensitively; an empty/None service means
/// "any service".
let private logBelongs (service : string option) (e : LogEntry) : bool =
  match service with
  | None -> true
  | Some s -> String.Equals(e.service, s, StringComparison.OrdinalIgnoreCase)

/// True when a trace touches `service` (any span's service matches). An
/// empty/None service means "any trace".
let private traceTouches (service : string option) (spans : Span array) : bool =
  match service with
  | None -> true
  | Some s -> spans |> Array.exists (fun sp -> String.Equals(sp.service, s, StringComparison.OrdinalIgnoreCase))

/// Compute a correlation snapshot for `(service, fromMs, toMs)`.
///   * logs  — entries in [fromMs, toMs] for the service, ranked + trimmed.
///   * trace — the slowest trace in the window touching the service.
let correlate (logStore   : LogStore)
              (spanStore  : ISpanStore)
              (tid        : TenantId)
              (service    : string option)
              (fromMs     : int64)
              (toMs       : int64)
              (logLimit   : int) : CorrelationSnapshot =
  // Logs: snapshot the ring, window it, filter by service, then rank.
  let logs =
    logStore.Snapshot()
    |> Array.filter (fun e -> e.ts >= fromMs && e.ts <= toMs && logBelongs service e)
    |> rankLogs logLimit

  // Traces: ask the span store for summaries overlapping the window, keep
  // those that touch the service, and pick the slowest by duration. We fetch
  // the full span set only for the chosen trace to keep this cheap.
  let slowest =
    spanStore.Traces(tid, fromMs, 500)
    |> Array.filter (fun t -> t.startMs <= toMs)
    |> Array.sortByDescending (fun t -> t.durationMs)
    |> Array.tryPick (fun t ->
      let spans = spanStore.GetTrace(tid, t.traceId)
      if spans.Length > 0 && traceTouches service spans
      then Some { summary = t; spans = spans }
      else None)

  { service      = service
    fromMs       = fromMs
    toMs         = toMs
    logs         = logs
    slowestTrace = slowest
    generatedAt  = nowMs () }

// -- exemplars --------------------------------------------------------------

[<NoComparison>]
type Exemplar =
  { traceId    : string
    service    : string
    operation  : string
    ts         : int64
    durationMs : int64
    error      : bool }

/// Derive trace exemplars for a `(service, fromMs, toMs)` window directly
/// from the span store. We take, per trace, the root (or longest) span that
/// touches the service and surface it as an exemplar point. Errors are
/// flagged so the UI can colour them. No separate exemplar ingest path is
/// required — this is the "exemplars by default" promise of 14.4.
let exemplarsFor (spanStore : ISpanStore)
                 (tid       : TenantId)
                 (service   : string option)
                 (fromMs    : int64)
                 (toMs      : int64)
                 (limit     : int) : Exemplar array =
  spanStore.Traces(tid, fromMs, 1000)
  |> Array.filter (fun t -> t.startMs <= toMs)
  |> Array.choose (fun t ->
    let spans = spanStore.GetTrace(tid, t.traceId)
    let relevant =
      match service with
      | None   -> spans
      | Some s -> spans |> Array.filter (fun sp -> String.Equals(sp.service, s, StringComparison.OrdinalIgnoreCase))
    if relevant.Length = 0 then None
    else
      // Pick the longest relevant span as the exemplar anchor.
      let sp = relevant |> Array.maxBy duration
      Some { traceId    = t.traceId
             service    = sp.service
             operation  = sp.operation
             ts         = sp.startMs
             durationMs = duration sp
             error      = relevant |> Array.exists isError })
  |> Array.sortByDescending (fun e -> e.ts)
  |> fun xs -> if xs.Length <= limit then xs else xs.[.. limit - 1]

// -- fire-time snapshot cache -----------------------------------------------

/// How far back/forward around an alert's fire time we look for correlated
/// signals. Alerts fire on the most-recent breaching sample, so the window
/// is mostly "just before now"; a small forward slop catches logs/spans that
/// land in the same second.
let private windowBeforeMs = 5L * 60L * 1000L   // 5 min lookback
let private windowAfterMs  = 30L * 1000L        // 30 s slop
let private notifyLogLimit = 3                  // top-N lines embedded in notifications
let private viewLogLimit   = 20                 // lines kept for the portal view
let private maxSnapshotsPerTenant = 500

/// Observes the alert sink and freezes a correlation snapshot when an alert
/// starts firing. Wire `Observe` into the same fan-out sink that drives
/// routing + runbooks (see `Program.fs`). Snapshots are read back by the
/// REST surface and by the notification renderer.
type Snapshotter(logStore : LogStore, spanStore : ISpanStore) =
  // (tenantId) -> (fingerprint -> snapshot). Bounded per tenant.
  let cache = ConcurrentDictionary<string, ConcurrentDictionary<string, CorrelationSnapshot>>()

  let bucket (TenantId t) =
    cache.GetOrAdd(t, fun _ -> ConcurrentDictionary<string, CorrelationSnapshot>())

  /// Capture (or refresh) the snapshot for a firing alert.
  member _.Capture(a : AlertInstance) : CorrelationSnapshot =
    let now = nowMs ()
    let firedAt = a.firedAt |> Option.defaultValue now
    let service = deriveService a.labels
    let snap =
      correlate logStore spanStore a.tenantId service
        (firedAt - windowBeforeMs) (firedAt + windowAfterMs) viewLogLimit
    let b = bucket a.tenantId
    // Bound the per-tenant cache: clear the whole bucket on overflow rather
    // than tracking insertion order — incidents are short-lived and the cap
    // is generous, so this is rare and cheap.
    if b.Count >= maxSnapshotsPerTenant && not (b.ContainsKey a.fingerprint) then
      b.Clear()
    b.[a.fingerprint] <- snap
    snap

  /// Sink hook: snapshot on the first firing observation; leave existing
  /// snapshots untouched on subsequent ticks so the window stays anchored to
  /// the original breach.
  member this.Observe(a : AlertInstance) =
    match a.state with
    | AlertState.Firing ->
      let b = bucket a.tenantId
      if not (b.ContainsKey a.fingerprint) then
        this.Capture a |> ignore
    | AlertState.Resolved ->
      // Keep the snapshot around for the post-incident view; it ages out
      // with the bounded cache. Nothing to do here.
      ()
    | AlertState.Pending -> ()

  /// Read a previously captured snapshot.
  member _.TryGet(tid : TenantId, fp : string) : CorrelationSnapshot option =
    match (bucket tid).TryGetValue fp with
    | true, s -> Some s
    | _       -> None

// -- JSON -------------------------------------------------------------------

let private writeLog (w : Utf8JsonWriter) (e : LogEntry) =
  w.WriteStartObject()
  w.WriteNumber("ts",      e.ts)
  w.WriteString("service", e.service)
  w.WriteString("level",   e.level)
  w.WriteString("message", e.message)
  w.WriteEndObject()

let private writeSpan (w : Utf8JsonWriter) (s : Span) =
  w.WriteStartObject()
  w.WriteString("traceId",    s.traceId)
  w.WriteString("spanId",     s.spanId)
  w.WriteString("parentSpanId", s.parentSpanId)
  w.WriteString("service",    s.service)
  w.WriteString("operation",  s.operation)
  w.WriteNumber("startMs",    s.startMs)
  w.WriteNumber("endMs",      s.endMs)
  w.WriteNumber("durationMs", duration s)
  w.WriteNumber("statusCode", s.statusCode)
  w.WriteBoolean("error",     isError s)
  w.WriteEndObject()

let private writeTraceSummary (w : Utf8JsonWriter) (t : TraceSummary) =
  w.WriteStartObject()
  w.WriteString("traceId",       t.traceId)
  w.WriteString("rootService",   t.rootService)
  w.WriteString("rootOperation", t.rootOperation)
  w.WriteNumber("startMs",       t.startMs)
  w.WriteNumber("durationMs",    t.durationMs)
  w.WriteNumber("spanCount",     t.spanCount)
  w.WriteNumber("errorCount",    t.errorCount)
  w.WritePropertyName "services"
  w.WriteStartArray()
  for s in t.services do w.WriteStringValue s
  w.WriteEndArray()
  w.WriteEndObject()

let writeSnapshot (w : Utf8JsonWriter) (s : CorrelationSnapshot) =
  w.WriteStartObject()
  (match s.service with Some sv -> w.WriteString("service", sv) | None -> w.WriteNull "service")
  w.WriteNumber("fromMs",      s.fromMs)
  w.WriteNumber("toMs",        s.toMs)
  w.WriteNumber("generatedAt", s.generatedAt)
  w.WritePropertyName "logs"
  w.WriteStartArray()
  for e in s.logs do writeLog w e
  w.WriteEndArray()
  w.WritePropertyName "slowestTrace"
  match s.slowestTrace with
  | None -> w.WriteNullValue()
  | Some t ->
    w.WriteStartObject()
    w.WritePropertyName "summary"
    writeTraceSummary w t.summary
    w.WritePropertyName "spans"
    w.WriteStartArray()
    for sp in t.spans do writeSpan w sp
    w.WriteEndArray()
    w.WriteEndObject()
  w.WriteEndObject()

let serialiseSnapshot (s : CorrelationSnapshot) : string =
  use ms = new IO.MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    writeSnapshot w s)
  Encoding.UTF8.GetString(ms.ToArray())

let serialiseExemplars (xs : Exemplar array) : string =
  use ms = new IO.MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    w.WriteStartArray()
    for e in xs do
      w.WriteStartObject()
      w.WriteString("traceId",    e.traceId)
      w.WriteString("service",    e.service)
      w.WriteString("operation",  e.operation)
      w.WriteNumber("ts",         e.ts)
      w.WriteNumber("durationMs", e.durationMs)
      w.WriteBoolean("error",     e.error)
      w.WriteEndObject()
    w.WriteEndArray())
  Encoding.UTF8.GetString(ms.ToArray())

/// One-line plain-text digest of a snapshot, embedded into notification
/// bodies (Slack / webhook). Kept terse: the top log lines and the slowest
/// trace id + duration.
let notificationDigest (s : CorrelationSnapshot) : string =
  let sb = StringBuilder()
  let topLogs = if s.logs.Length <= notifyLogLimit then s.logs else s.logs.[.. notifyLogLimit - 1]
  if topLogs.Length > 0 then
    sb.Append "Top logs:" |> ignore
    for e in topLogs do
      let msg = if e.message.Length > 160 then e.message.Substring(0, 157) + "…" else e.message
      sb.AppendFormat("\n  [{0}] {1}", (if String.IsNullOrEmpty e.level then "log" else e.level), msg) |> ignore
  match s.slowestTrace with
  | Some t ->
    sb.AppendFormat("\nSlowest trace: {0} ({1} · {2}ms, {3} spans, {4} errors)",
                    t.summary.traceId, t.summary.rootService,
                    t.summary.durationMs, t.summary.spanCount, t.summary.errorCount) |> ignore
  | None -> ()
  sb.ToString()

// -- REST -------------------------------------------------------------------

let private jsonOk (body : string) : WebPart =
  OK body >=> Writers.setMimeType "application/json"

let private errJson (status : int) (msg : string) : WebPart =
  let body = sprintf """{"error":%s}""" (JsonSerializer.Serialize msg)
  let writer =
    match status with
    | 400 -> BAD_REQUEST
    | 401 -> Suave.RequestErrors.UNAUTHORIZED
    | 404 -> NOT_FOUND
    | _   -> Suave.ServerErrors.INTERNAL_ERROR
  writer body >=> Writers.setMimeType "application/json"

let private resolveTenant (multiTenant : bool) (ctx : HttpContext) : TenantId option =
  if multiTenant then
    PulseBoard.Rbac.tryGetTenant ctx |> Option.map (fun t -> t.tenant.id)
  else Some (TenantId "__local__")

let private parseInt64 (q : HttpRequest) (name : string) (dflt : int64) : int64 =
  match q.queryParam name with
  | Choice1Of2 v -> let mutable n = 0L in (if Int64.TryParse(v, &n) then n else dflt)
  | _ -> dflt

let private parseInt (q : HttpRequest) (name : string) (dflt : int) : int =
  match q.queryParam name with
  | Choice1Of2 v -> let mutable n = 0 in (if Int32.TryParse(v, &n) then n else dflt)
  | _ -> dflt

let private parseStr (q : HttpRequest) (name : string) : string option =
  match q.queryParam name with
  | Choice1Of2 v when not (String.IsNullOrWhiteSpace v) -> Some v
  | _ -> None

/// Build the correlation REST surface.
///   GET /api/alerts/<fp>/correlation       — fire-time snapshot for an alert
///   GET /api/correlate?service=&fromMs=&toMs=&limit= — ad-hoc window correlation
///   GET /api/exemplars?service=&fromMs=&toMs=&limit= — trace exemplars for a window
///
/// `resolveActive` lets the alert-correlation route fall back to a live
/// snapshot when an alert fired before the snapshotter observed it (e.g. the
/// snapshot cache was cleared, or the process just restarted).
let webPart (multiTenant : bool)
            (logStore     : LogStore)
            (spanStore    : ISpanStore)
            (snapshotter  : Snapshotter)
            (resolveActive : TenantId -> string -> AlertInstance option) : WebPart =

  let withTenant (handler : TenantId -> WebPart) : WebPart =
    fun ctx -> async {
      match resolveTenant multiTenant ctx with
      | None     -> return! errJson 401 "no tenant in request" ctx
      | Some tid -> return! handler tid ctx
    }

  let alertCorrelation (fp : string) : WebPart =
    withTenant (fun tid ->
      fun ctx -> async {
        let snap =
          match snapshotter.TryGet(tid, fp) with
          | Some s -> Some s
          | None ->
            // No frozen snapshot — compute one live from the active alert.
            match resolveActive tid fp with
            | Some a -> Some (snapshotter.Capture a)
            | None   -> None
        match snap with
        | Some s -> return! jsonOk (serialiseSnapshot s) ctx
        | None   -> return! errJson 404 ("no correlation for alert " + fp) ctx
      })

  let adHocCorrelation : WebPart =
    withTenant (fun tid ->
      fun ctx -> async {
        let now    = nowMs ()
        let toMs   = parseInt64 ctx.request "toMs" now
        let fromMs = parseInt64 ctx.request "fromMs" (toMs - windowBeforeMs)
        let service = parseStr ctx.request "service"
        let limit  = max 1 (min 200 (parseInt ctx.request "limit" viewLogLimit))
        let snap = correlate logStore spanStore tid service fromMs toMs limit
        return! jsonOk (serialiseSnapshot snap) ctx
      })

  let exemplars : WebPart =
    withTenant (fun tid ->
      fun ctx -> async {
        let now    = nowMs ()
        let toMs   = parseInt64 ctx.request "toMs" now
        let fromMs = parseInt64 ctx.request "fromMs" (toMs - windowBeforeMs)
        let service = parseStr ctx.request "service"
        let limit  = max 1 (min 500 (parseInt ctx.request "limit" 100))
        let xs = exemplarsFor spanStore tid service fromMs toMs limit
        return! jsonOk (serialiseExemplars xs) ctx
      })

  choose [
    GET >=> pathScan "/api/alerts/%s/correlation" alertCorrelation
    GET >=> path     "/api/correlate"           >=> adHocCorrelation
    GET >=> path     "/api/exemplars"           >=> exemplars
  ]

module PulseBoard.Otlp

open System
open System.IO
open System.Text
open System.Text.Json
open Suave
open Suave.Operators
open Suave.Successful
open Suave.RequestErrors
open Google.Protobuf
open PulseBoard.TimeSeries
open PulseBoard.Hub
open PulseBoard.Tenancy
open PulseBoard.Quotas
open PulseBoard.Audit
open PulseBoard.Ingest

// OpenTelemetry OTLP/HTTP receiver. The wire format is protobuf-encoded
// ExportXServiceRequest from open-telemetry/opentelemetry-proto. We hand-
// decode only the subset of fields we care about, same as PromRemoteWrite:
//
//   * Metrics: ResourceMetrics > ScopeMetrics > Metric > {Gauge|Sum} >
//              NumberDataPoint. Histogram, ExponentialHistogram and
//              Summary are skipped — Phase 3 will land sketch storage.
//   * Logs:    ResourceLogs > ScopeLogs > LogRecord (time, severity,
//              body, attrs).
//   * Traces:  spans are counted only; storage waits for the Tempo
//              backend in Phase 3.
//
// Content-Type: only application/x-protobuf is accepted in v1. The
// application/json OTLP encoding (Proto3 JSON) can land later behind the
// same handlers via a content-type fork.

[<Struct>]
type private AnyVal =
  | VStr  of strv:string
  | VBool of boolv:bool
  | VInt  of intv:int64
  | VDbl  of dblv:float
  | VNone

[<Struct>] type private Attr = { key : string; value : AnyVal }

let private valueText (v : AnyVal) =
  match v with
  | VStr s  -> s
  | VBool b -> if b then "true" else "false"
  | VInt i  -> string i
  | VDbl d  -> d.ToString(System.Globalization.CultureInfo.InvariantCulture)
  | VNone   -> ""

let private fieldOf (tag : uint32) = int (tag >>> 3)

// ---------- Common decoders ----------

let private decodeAnyValue (bytes : ByteString) : AnyVal =
  let input = new CodedInputStream(bytes.ToByteArray())
  let mutable v = VNone
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> v <- VStr  (input.ReadString())
    | 2 -> v <- VBool (input.ReadBool())
    | 3 -> v <- VInt  (input.ReadInt64())
    | 4 -> v <- VDbl  (input.ReadDouble())
    | _ -> input.SkipLastField()
  v

let private decodeKeyValue (bytes : ByteString) : Attr =
  let input = new CodedInputStream(bytes.ToByteArray())
  let mutable k = ""
  let mutable v = VNone
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> k <- input.ReadString()
    | 2 -> v <- decodeAnyValue (input.ReadBytes())
    | _ -> input.SkipLastField()
  { key = k; value = v }

let private decodeResource (bytes : ByteString) : Attr[] =
  let input = new CodedInputStream(bytes.ToByteArray())
  let attrs = ResizeArray<Attr>()
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> attrs.Add(decodeKeyValue (input.ReadBytes()))
    | _ -> input.SkipLastField()
  attrs.ToArray()

let private decodeScope (bytes : ByteString) : string =
  let input = new CodedInputStream(bytes.ToByteArray())
  let mutable name = ""
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> name <- input.ReadString()
    | _ -> input.SkipLastField()
  name

// ---------- Metrics decoders ----------

[<Struct>]
type private DataPoint =
  { attrs    : Attr[]
    tsNano   : uint64
    value    : float
    hasValue : bool }

let private decodeNumberDataPoint (bytes : ByteString) : DataPoint =
  let input = new CodedInputStream(bytes.ToByteArray())
  let attrs = ResizeArray<Attr>()
  let mutable tsNano = 0UL
  let mutable value  = 0.0
  let mutable hasV   = false
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 2 -> input.ReadFixed64() |> ignore           // start_time_unix_nano
    | 3 -> tsNano <- input.ReadFixed64()           // time_unix_nano
    | 4 -> value <- input.ReadDouble();           hasV <- true   // as_double
    | 6 -> value <- float (input.ReadSFixed64()); hasV <- true   // as_int
    | 7 -> attrs.Add(decodeKeyValue (input.ReadBytes()))
    | _ -> input.SkipLastField()
  { attrs = attrs.ToArray(); tsNano = tsNano; value = value; hasValue = hasV }

let private decodeNumberPoints (bytes : ByteString) : DataPoint[] =
  // Both `Gauge` and `Sum` have `repeated NumberDataPoint data_points = 1`.
  let input = new CodedInputStream(bytes.ToByteArray())
  let pts = ResizeArray<DataPoint>()
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> pts.Add(decodeNumberDataPoint (input.ReadBytes()))
    | _ -> input.SkipLastField()
  pts.ToArray()

type private MetricRec = { name : string; points : DataPoint[] }

let private decodeMetric (bytes : ByteString) : MetricRec =
  let input = new CodedInputStream(bytes.ToByteArray())
  let mutable name = ""
  let mutable pts  : DataPoint[] = [||]
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> name <- input.ReadString()
    | 5 -> pts  <- decodeNumberPoints (input.ReadBytes())   // Gauge
    | 7 -> pts  <- decodeNumberPoints (input.ReadBytes())   // Sum
    | _ -> input.SkipLastField()
  { name = name; points = pts }

type private ScopeMetricsRec = { scope : string; metrics : MetricRec[] }

let private decodeScopeMetrics (bytes : ByteString) : ScopeMetricsRec =
  let input = new CodedInputStream(bytes.ToByteArray())
  let mutable scopeName = ""
  let metrics = ResizeArray<MetricRec>()
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> scopeName <- decodeScope (input.ReadBytes())
    | 2 -> metrics.Add(decodeMetric (input.ReadBytes()))
    | _ -> input.SkipLastField()
  { scope = scopeName; metrics = metrics.ToArray() }

type private ResourceMetricsRec = { resource : Attr[]; scopes : ScopeMetricsRec[] }

let private decodeResourceMetrics (bytes : ByteString) : ResourceMetricsRec =
  let input = new CodedInputStream(bytes.ToByteArray())
  let mutable res : Attr[] = [||]
  let scopes = ResizeArray<ScopeMetricsRec>()
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> res <- decodeResource (input.ReadBytes())
    | 2 -> scopes.Add(decodeScopeMetrics (input.ReadBytes()))
    | _ -> input.SkipLastField()
  { resource = res; scopes = scopes.ToArray() }

let private decodeExportMetricsRequest (input : CodedInputStream) : ResourceMetricsRec[] =
  let arr = ResizeArray<ResourceMetricsRec>()
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> arr.Add(decodeResourceMetrics (input.ReadBytes()))
    | _ -> input.SkipLastField()
  arr.ToArray()

// ---------- Logs decoders ----------

type private LogRec =
  { tsNano  : uint64
    sevNum  : int
    sevText : string
    body    : AnyVal
    attrs   : Attr[] }

let private decodeLogRecord (bytes : ByteString) : LogRec =
  let input = new CodedInputStream(bytes.ToByteArray())
  let mutable tsNano = 0UL
  let mutable sevNum = 0
  let mutable sevTxt = ""
  let mutable body   = VNone
  let attrs = ResizeArray<Attr>()
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1  -> tsNano <- input.ReadFixed64()
    | 2  -> sevNum <- input.ReadEnum()
    | 3  -> sevTxt <- input.ReadString()
    | 5  -> body   <- decodeAnyValue (input.ReadBytes())
    | 6  -> attrs.Add(decodeKeyValue (input.ReadBytes()))
    | 11 -> input.ReadFixed64() |> ignore   // observed_time_unix_nano
    | _  -> input.SkipLastField()
  { tsNano = tsNano; sevNum = sevNum; sevText = sevTxt
    body   = body;   attrs  = attrs.ToArray() }

type private ScopeLogsRec = { scope : string; logs : LogRec[] }

let private decodeScopeLogs (bytes : ByteString) : ScopeLogsRec =
  let input = new CodedInputStream(bytes.ToByteArray())
  let mutable scopeName = ""
  let logs = ResizeArray<LogRec>()
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> scopeName <- decodeScope (input.ReadBytes())
    | 2 -> logs.Add(decodeLogRecord (input.ReadBytes()))
    | _ -> input.SkipLastField()
  { scope = scopeName; logs = logs.ToArray() }

type private ResourceLogsRec = { resource : Attr[]; scopes : ScopeLogsRec[] }

let private decodeResourceLogs (bytes : ByteString) : ResourceLogsRec =
  let input = new CodedInputStream(bytes.ToByteArray())
  let mutable res : Attr[] = [||]
  let scopes = ResizeArray<ScopeLogsRec>()
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> res <- decodeResource (input.ReadBytes())
    | 2 -> scopes.Add(decodeScopeLogs (input.ReadBytes()))
    | _ -> input.SkipLastField()
  { resource = res; scopes = scopes.ToArray() }

let private decodeExportLogsRequest (input : CodedInputStream) : ResourceLogsRec[] =
  let arr = ResizeArray<ResourceLogsRec>()
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> arr.Add(decodeResourceLogs (input.ReadBytes()))
    | _ -> input.SkipLastField()
  arr.ToArray()

// ---------- Trace span counting ----------

let private countSpansScope (bytes : ByteString) : int =
  let input = new CodedInputStream(bytes.ToByteArray())
  let mutable n = 0
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 2 ->
      input.ReadBytes() |> ignore   // skip span body
      n <- n + 1
    | _ -> input.SkipLastField()
  n

let private countSpansResource (bytes : ByteString) : int =
  let input = new CodedInputStream(bytes.ToByteArray())
  let mutable n = 0
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 2 -> n <- n + countSpansScope (input.ReadBytes())
    | _ -> input.SkipLastField()
  n

let private countSpans (input : CodedInputStream) : int =
  let mutable n = 0
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> n <- n + countSpansResource (input.ReadBytes())
    | _ -> input.SkipLastField()
  n

// ---------- Series naming ----------

let private escapeLabel (sb : StringBuilder) (value : string) =
  for c in value do
    match c with
    | '\\' -> sb.Append "\\\\" |> ignore
    | '"'  -> sb.Append "\\\"" |> ignore
    | '\n' -> sb.Append "\\n"  |> ignore
    | _    -> sb.Append c       |> ignore

/// Canonical Prometheus-style series name built from the metric name plus
/// the union of (resource attrs ∪ otel_scope_name ∪ point attrs), sorted
/// by key. Matches the naming used by `PromRemoteWrite.seriesName` so
/// cardinality admission, alert lookups and the query API see one
/// universe of series names regardless of receiver.
let private buildSeriesName (metric : string) (resource : Attr[])
                            (scope : string) (point : DataPoint) : string =
  let merged = ResizeArray<struct (string * string)>()
  for a in resource do merged.Add(struct (a.key, valueText a.value))
  if scope.Length > 0 then merged.Add(struct ("otel_scope_name", scope))
  for a in point.attrs do merged.Add(struct (a.key, valueText a.value))
  if merged.Count = 0 then metric
  else
    let arr = merged.ToArray()
    Array.sortInPlaceBy (fun (struct (k, _)) -> k) arr
    let sb = StringBuilder(metric)
    sb.Append '{' |> ignore
    for i in 0 .. arr.Length - 1 do
      if i > 0 then sb.Append ',' |> ignore
      let struct (k, v) = arr.[i]
      sb.Append k    |> ignore
      sb.Append "=\"" |> ignore
      escapeLabel sb v
      sb.Append '"'  |> ignore
    sb.Append '}' |> ignore
    sb.ToString()

// ---------- Audit / publish helpers ----------

let private auditDeny (q : IngestQuotas) (ctx : HttpContext)
                      (action : string) (details : string) =
  let t = PulseBoard.Rbac.tryGetTenant ctx
  let ev : AuditEvent =
    { ts       = DateTimeOffset.UtcNow
      tenant   = t |> Option.map (fun x -> x.tenant.id)
      apiKeyId = t |> Option.map (fun x -> x.apiKeyId)
      action   = action
      resource = ctx.request.path
      outcome  = Deny
      remoteIp = None
      details  = Some details }
  try q.auditLog.Append ev with _ -> ()

let private publishMetric (hub : Broadcaster) (name : string) (p : Point) =
  let json =
    sprintf """{"type":"metric","name":%s,"ts":%d,"value":%s}"""
      (JsonSerializer.Serialize name)
      p.ts
      (p.value.ToString(System.Globalization.CultureInfo.InvariantCulture))
  hub.Publish json

let private publishLog (hub : Broadcaster) (e : LogEntry) =
  let json =
    sprintf """{"type":"log","ts":%d,"service":%s,"level":%s,"message":%s}"""
      e.ts
      (JsonSerializer.Serialize e.service)
      (JsonSerializer.Serialize e.level)
      (JsonSerializer.Serialize e.message)
  hub.Publish json

let private severityName (n : int) (text : string) =
  if text.Length > 0 then text.ToLowerInvariant()
  else
    match n with
    | x when x >= 17 -> "fatal"
    | x when x >= 13 -> "error"
    | x when x >=  9 -> "warn"
    | x when x >=  5 -> "info"
    | x when x >=  1 -> "debug"
    | _ -> "info"

let private attrLookup (attrs : Attr[]) (key : string) : string option =
  attrs |> Array.tryPick (fun a ->
    if a.key = key then Some (valueText a.value) else None)

let private okHeaders accepted : WebPart =
  Writers.setMimeType "application/json"
  >=> Writers.setHeader "X-PulseBoard-Accepted" (string accepted)

let private partialSuccessBody = """{"partialSuccess":{}}"""

// ---------- Handlers ----------

/// POST /v1/metrics — OTLP/HTTP metrics. Body is a protobuf
/// `ExportMetricsServiceRequest`. We map every NumberDataPoint of every
/// Gauge / Sum metric to a `Point` in `MetricStore`, naming series with
/// resource attrs ∪ scope name ∪ point attrs. Histograms / summaries are
/// silently ignored for now.
let metrics (store : MetricStore) (hub : Broadcaster)
            (quotas : IngestQuotas option) : WebPart =
  fun ctx -> async {
    try
      let raw = ctx.request.rawForm
      if isNull raw || raw.Length = 0 then
        return! BAD_REQUEST """{"error":"empty body"}""" ctx
      else
      use ms = new MemoryStream(raw)
      let input = new CodedInputStream(ms)
      let resourceMetrics = decodeExportMetricsRequest input
      let tenantId =
        PulseBoard.Rbac.tryGetTenant ctx
        |> Option.map (fun t -> t.tenant.id)
      let mutable accepted = 0
      for rm in resourceMetrics do
        for sm in rm.scopes do
          for m in sm.metrics do
            for p in m.points do
              if p.hasValue && m.name.Length > 0 then
                let name = buildSeriesName m.name rm.resource sm.scope p
                let admit =
                  match quotas, tenantId with
                  | Some q, Some tid ->
                    match q.limiter.TryAdmitSeries(tid, name) with
                    | CardinalityResult.Ok -> true
                    | CardinalityResult.Rejected cap ->
                      auditDeny q ctx "quota.cardinality"
                        (sprintf "series=%s cap=%d" name cap)
                      false
                  | _ -> true
                if admit then
                  let tsMs =
                    if p.tsNano > 0UL then int64 (p.tsNano / 1_000_000UL)
                    else DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                  let pt : Point = { ts = tsMs; value = p.value }
                  store.Record(name, pt)
                  publishMetric hub name pt
                  accepted <- accepted + 1
      return! (OK partialSuccessBody >=> okHeaders accepted) ctx
    with ex ->
      return!
        BAD_REQUEST
          (sprintf """{"error":%s}"""
             (JsonSerializer.Serialize ex.Message)) ctx
  }

/// POST /v1/logs — OTLP/HTTP logs. Body length charged against the
/// tenant's LogBytes bucket before parsing; over-quota → 429.
let logs (store : LogStore) (hub : Broadcaster)
         (quotas : IngestQuotas option) : WebPart =
  fun ctx -> async {
    try
      let raw = ctx.request.rawForm
      if isNull raw || raw.Length = 0 then
        return! BAD_REQUEST """{"error":"empty body"}""" ctx
      else
      let tenantId =
        PulseBoard.Rbac.tryGetTenant ctx
        |> Option.map (fun t -> t.tenant.id)
      let throttle =
        match quotas, tenantId with
        | Some q, Some tid ->
          match q.limiter.TryAcquire(tid, LogBytes, float raw.Length) with
          | AcquireResult.Ok -> None
          | AcquireResult.Throttled ms ->
            auditDeny q ctx "quota.logBytes"
              (sprintf "bytes=%d retryAfterMs=%d" raw.Length ms)
            Some ms
        | _ -> None
      match throttle with
      | Some ms ->
        let retrySec = max 1 (int (ceil (float ms / 1000.0)))
        let body =
          sprintf
            """{"error":"rate limit exceeded","kind":"logBytes","retryAfterMs":%d}""" ms
        return!
          (TOO_MANY_REQUESTS body
           >=> Writers.setMimeType "application/json"
           >=> Writers.setHeader "Retry-After" (string retrySec)) ctx
      | None ->
        use mstream = new MemoryStream(raw)
        let input = new CodedInputStream(mstream)
        let resourceLogs = decodeExportLogsRequest input
        let mutable accepted = 0
        for rl in resourceLogs do
          let service =
            attrLookup rl.resource "service.name"
            |> Option.defaultValue "unknown"
          for sl in rl.scopes do
            for lr in sl.logs do
              let tsMs =
                if lr.tsNano > 0UL then int64 (lr.tsNano / 1_000_000UL)
                else DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
              let level = severityName lr.sevNum lr.sevText
              let message = valueText lr.body
              let entry : LogEntry =
                { ts = tsMs; service = service; level = level; message = message }
              store.Add entry
              publishLog hub entry
              accepted <- accepted + 1
        return! (OK partialSuccessBody >=> okHeaders accepted) ctx
    with ex ->
      return!
        BAD_REQUEST
          (sprintf """{"error":%s}"""
             (JsonSerializer.Serialize ex.Message)) ctx
  }

/// POST /v1/traces — accepted and counted only. Storage will land with
/// the Tempo backend in Phase 3; until then we return a successful
/// `partialSuccess:{}` so OTel SDKs don't retry / back off.
let traces : WebPart =
  fun ctx -> async {
    try
      let raw = ctx.request.rawForm
      if isNull raw || raw.Length = 0 then
        return! BAD_REQUEST """{"error":"empty body"}""" ctx
      else
      use ms = new MemoryStream(raw)
      let input = new CodedInputStream(ms)
      let n = countSpans input
      return! (OK partialSuccessBody >=> okHeaders n) ctx
    with ex ->
      return!
        BAD_REQUEST
          (sprintf """{"error":%s}"""
             (JsonSerializer.Serialize ex.Message)) ctx
  }

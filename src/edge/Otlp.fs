module PulseBoard.Otlp

open System
open System.IO
open System.IO.Compression
open System.Text
open System.Text.Json
open Suave
open Suave.Operators
open Suave.Successful
open Suave.RequestErrors
open Google.Protobuf
open PulseBoard.TimeSeries
open PulseBoard.Tenancy
open PulseBoard.Quotas
open PulseBoard.Audit
open PulseBoard.Gateway
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
// Content-Type: both application/x-protobuf and application/json
// (OTLP/HTTP Proto3 JSON) are accepted on /v1/metrics and /v1/traces.
// /v1/logs is still protobuf-only — Phase 16 tracers don't ship logs.

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

// ---------- Trace span decoding ----------

let private hex (bs : ByteString) : string =
  let bytes = bs.ToByteArray()
  let sb = StringBuilder(bytes.Length * 2)
  for b in bytes do sb.AppendFormat("{0:x2}", b) |> ignore
  sb.ToString()

let private decodeStatus (bytes : ByteString) : int =
  let input = new CodedInputStream(bytes.ToByteArray())
  let mutable code = 0
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 3 -> code <- input.ReadEnum()
    | _ -> input.SkipLastField()
  code

type private SpanRec =
  { traceId  : string
    spanId   : string
    parentId : string
    name     : string
    kind     : int
    startNs  : uint64
    endNs    : uint64
    status   : int
    attrs    : Attr[] }

let private decodeSpan (bytes : ByteString) : SpanRec =
  let input = new CodedInputStream(bytes.ToByteArray())
  let mutable traceId = ""
  let mutable spanId  = ""
  let mutable parent  = ""
  let mutable name    = ""
  let mutable kind    = 0
  let mutable startNs = 0UL
  let mutable endNs   = 0UL
  let mutable status  = 0
  let attrs = ResizeArray<Attr>()
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1  -> traceId <- hex (input.ReadBytes())
    | 2  -> spanId  <- hex (input.ReadBytes())
    | 4  -> parent  <- hex (input.ReadBytes())
    | 5  -> name    <- input.ReadString()
    | 6  -> kind    <- input.ReadEnum()
    | 7  -> startNs <- input.ReadFixed64()
    | 8  -> endNs   <- input.ReadFixed64()
    | 9  -> attrs.Add(decodeKeyValue (input.ReadBytes()))
    | 15 -> status  <- decodeStatus (input.ReadBytes())
    | _  -> input.SkipLastField()
  { traceId = traceId; spanId = spanId; parentId = parent
    name = name; kind = kind; startNs = startNs; endNs = endNs
    status = status; attrs = attrs.ToArray() }

type private ScopeSpansRec = { spans : SpanRec[] }

let private decodeScopeSpans (bytes : ByteString) : ScopeSpansRec =
  let input = new CodedInputStream(bytes.ToByteArray())
  let spans = ResizeArray<SpanRec>()
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 2 -> spans.Add(decodeSpan (input.ReadBytes()))
    | _ -> input.SkipLastField()
  { spans = spans.ToArray() }

type private ResourceSpansRec = { resource : Attr[]; scopes : ScopeSpansRec[] }

let private decodeResourceSpans (bytes : ByteString) : ResourceSpansRec =
  let input = new CodedInputStream(bytes.ToByteArray())
  let mutable res : Attr[] = [||]
  let scopes = ResizeArray<ScopeSpansRec>()
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> res <- decodeResource (input.ReadBytes())
    | 2 -> scopes.Add(decodeScopeSpans (input.ReadBytes()))
    | _ -> input.SkipLastField()
  { resource = res; scopes = scopes.ToArray() }

let private decodeExportTraceRequest (input : CodedInputStream) : ResourceSpansRec[] =
  let arr = ResizeArray<ResourceSpansRec>()
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> arr.Add(decodeResourceSpans (input.ReadBytes()))
    | _ -> input.SkipLastField()
  arr.ToArray()

/// Lift the intermediate `ResourceSpansRec[]` (produced either by the
/// protobuf decoder or by `decodeExportTraceJson`) into the public Span
/// model used by `PulseBoard.Spans`. Resource-level attrs are merged
/// into each span's attribute map; `service.name` is promoted to the
/// span's first-class `service` field (default "unknown" when absent).
/// Returns both the structured spans and the raw span count so the
/// existing billing path stays a single pass.
let private liftSpans (resources : ResourceSpansRec[]) : PulseBoard.Spans.Span[] * int =
  let out = ResizeArray<PulseBoard.Spans.Span>()
  let mutable count = 0
  for rs in resources do
    let resAttrs =
      rs.resource
      |> Array.map (fun a -> a.key, valueText a.value)
      |> Map.ofArray
    let service =
      resAttrs
      |> Map.tryFind "service.name"
      |> Option.defaultValue "unknown"
    for sc in rs.scopes do
      for s in sc.spans do
        count <- count + 1
        let attrs =
          let m = Map.ofArray (s.attrs |> Array.map (fun a -> a.key, valueText a.value))
          // Resource attrs win when both define the same key — they're
          // the more stable identity.
          Map.fold (fun acc k v -> Map.add k v acc) m resAttrs
        out.Add(
          { traceId      = s.traceId
            spanId       = s.spanId
            parentSpanId = s.parentId
            service      = service
            operation    = s.name
            kind         = PulseBoard.Spans.kindOfInt s.kind
            startMs      = int64 (s.startNs / 1_000_000UL)
            endMs        = int64 (s.endNs   / 1_000_000UL)
            statusCode   = s.status
            attributes   = attrs })
  out.ToArray(), count

/// Decode an OTLP/protobuf `ExportTraceServiceRequest` into the public
/// Span model. See `liftSpans` for the resource → span attribute merge
/// semantics.
let decodeSpans (raw : byte[]) : PulseBoard.Spans.Span[] * int =
  use ms = new MemoryStream(raw)
  let input = new CodedInputStream(ms)
  liftSpans (decodeExportTraceRequest input)

// Backwards-compatible counter used by the original `traces` handler.
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

// ---------- OTLP/JSON metrics decoder ----------
// The OTLP spec defines two wire formats for /v1/metrics: protobuf
// (application/x-protobuf) and Proto3 JSON (application/json). PulseAgent
// emits the JSON form, so we decode the same intermediate
// `ResourceMetricsRec[]` from JSON and feed it into the existing samples
// extraction loop.

let private jsonAnyValue (el : JsonElement) : AnyVal =
  if el.ValueKind <> JsonValueKind.Object then VNone
  else
    let mutable v = VNone
    for p in el.EnumerateObject() do
      match p.Name with
      | "stringValue" when p.Value.ValueKind = JsonValueKind.String ->
        v <- VStr (p.Value.GetString())
      | "boolValue"   when p.Value.ValueKind = JsonValueKind.True
                      || p.Value.ValueKind = JsonValueKind.False ->
        v <- VBool (p.Value.GetBoolean())
      | "intValue" ->
        match p.Value.ValueKind with
        | JsonValueKind.String ->
          match Int64.TryParse(p.Value.GetString()) with
          | true, n -> v <- VInt n
          | _ -> ()
        | JsonValueKind.Number -> v <- VInt (p.Value.GetInt64())
        | _ -> ()
      | "doubleValue" when p.Value.ValueKind = JsonValueKind.Number ->
        v <- VDbl (p.Value.GetDouble())
      | _ -> ()
    v

let private jsonAttrs (el : JsonElement) : Attr[] =
  let mutable arr : Attr[] = [||]
  let mutable found = false
  if el.ValueKind = JsonValueKind.Object then
    match el.TryGetProperty "attributes" with
    | true, a when a.ValueKind = JsonValueKind.Array ->
      found <- true
      arr <-
        a.EnumerateArray()
        |> Seq.map (fun kv ->
            let key =
              match kv.TryGetProperty "key" with
              | true, k when k.ValueKind = JsonValueKind.String -> k.GetString()
              | _ -> ""
            let value =
              match kv.TryGetProperty "value" with
              | true, v -> jsonAnyValue v
              | _ -> VNone
            { key = key; value = value })
        |> Seq.toArray
    | _ -> ()
  if found then arr else [||]

let private jsonTsNano (el : JsonElement) : uint64 =
  match el.TryGetProperty "timeUnixNano" with
  | true, t ->
    match t.ValueKind with
    | JsonValueKind.String ->
      match UInt64.TryParse(t.GetString()) with
      | true, n -> n
      | _ -> 0UL
    | JsonValueKind.Number -> t.GetUInt64()
    | _ -> 0UL
  | _ -> 0UL

let private jsonNumberDataPoint (el : JsonElement) : DataPoint =
  let attrs  = jsonAttrs el
  let tsNano = jsonTsNano el
  let mutable v = 0.0
  let mutable hasV = false
  match el.TryGetProperty "asDouble" with
  | true, x when x.ValueKind = JsonValueKind.Number ->
    v <- x.GetDouble(); hasV <- true
  | _ ->
    match el.TryGetProperty "asInt" with
    | true, x ->
      match x.ValueKind with
      | JsonValueKind.String ->
        match Int64.TryParse(x.GetString()) with
        | true, n -> v <- float n; hasV <- true
        | _ -> ()
      | JsonValueKind.Number ->
        v <- float (x.GetInt64()); hasV <- true
      | _ -> ()
    | _ -> ()
  { attrs = attrs; tsNano = tsNano; value = v; hasValue = hasV }

let private jsonNumberPoints (el : JsonElement) : DataPoint[] =
  if el.ValueKind <> JsonValueKind.Object then [||]
  else
    match el.TryGetProperty "dataPoints" with
    | true, dp when dp.ValueKind = JsonValueKind.Array ->
      dp.EnumerateArray() |> Seq.map jsonNumberDataPoint |> Seq.toArray
    | _ -> [||]

let private jsonMetric (el : JsonElement) : MetricRec =
  let name =
    match el.TryGetProperty "name" with
    | true, n when n.ValueKind = JsonValueKind.String -> n.GetString()
    | _ -> ""
  let pts =
    match el.TryGetProperty "gauge" with
    | true, g -> jsonNumberPoints g
    | _ ->
      match el.TryGetProperty "sum" with
      | true, s -> jsonNumberPoints s
      | _ -> [||]
  { name = name; points = pts }

let private jsonScopeMetrics (el : JsonElement) : ScopeMetricsRec =
  let scopeName =
    match el.TryGetProperty "scope" with
    | true, s ->
      match s.TryGetProperty "name" with
      | true, n when n.ValueKind = JsonValueKind.String -> n.GetString()
      | _ -> ""
    | _ -> ""
  let metrics =
    match el.TryGetProperty "metrics" with
    | true, m when m.ValueKind = JsonValueKind.Array ->
      m.EnumerateArray() |> Seq.map jsonMetric |> Seq.toArray
    | _ -> [||]
  { scope = scopeName; metrics = metrics }

let private jsonResourceMetrics (el : JsonElement) : ResourceMetricsRec =
  let resAttrs =
    match el.TryGetProperty "resource" with
    | true, r -> jsonAttrs r
    | _ -> [||]
  let scopes =
    match el.TryGetProperty "scopeMetrics" with
    | true, sm when sm.ValueKind = JsonValueKind.Array ->
      sm.EnumerateArray() |> Seq.map jsonScopeMetrics |> Seq.toArray
    | _ -> [||]
  { resource = resAttrs; scopes = scopes }

let private decodeExportMetricsJson (raw : byte[]) : ResourceMetricsRec[] =
  use doc = JsonDocument.Parse(ReadOnlyMemory<byte>(raw))
  let root = doc.RootElement
  match root.TryGetProperty "resourceMetrics" with
  | true, rm when rm.ValueKind = JsonValueKind.Array ->
    rm.EnumerateArray() |> Seq.map jsonResourceMetrics |> Seq.toArray
  | _ -> [||]

// ---------- OTLP/JSON trace decoder ----------
// Mirrors `decodeExportMetricsJson` for `ExportTraceServiceRequest`.
// Phase 16 tracers (Node `@open-pulseboard/tracer`, Python
// `pulseboard-tracer`) and the Phase 15 Slice 4 Step Functions
// `OtlpHttpSpanSink` both ship Proto3 JSON because the upstream OTLP
// HTTP exporter doesn't speak JSON for traces but the F# Firehose
// translator only emits JSON and the OSS edge is the only consumer.

let private jsonHex (el : JsonElement) : string =
  // OTLP/JSON encodes trace_id and span_id as lower-hex strings
  // (16 / 8 bytes → 32 / 16 chars). Some encoders also accept
  // base64; we don't, because the spec is hex-only.
  match el.ValueKind with
  | JsonValueKind.String -> el.GetString().ToLowerInvariant()
  | _ -> ""

let private jsonUint64Field (el : JsonElement) (name : string) : uint64 =
  match el.TryGetProperty name with
  | true, v ->
    match v.ValueKind with
    | JsonValueKind.String ->
      match UInt64.TryParse(v.GetString()) with
      | true, n -> n
      | _ -> 0UL
    | JsonValueKind.Number -> v.GetUInt64()
    | _ -> 0UL
  | _ -> 0UL

let private jsonIntField (el : JsonElement) (name : string) : int =
  match el.TryGetProperty name with
  | true, v ->
    match v.ValueKind with
    | JsonValueKind.Number -> v.GetInt32()
    | JsonValueKind.String ->
      match Int32.TryParse(v.GetString()) with
      | true, n -> n
      | _ -> 0
    | _ -> 0
  | _ -> 0

let private jsonStatusCode (el : JsonElement) : int =
  match el.TryGetProperty "status" with
  | true, s when s.ValueKind = JsonValueKind.Object -> jsonIntField s "code"
  | _ -> 0

let private jsonSpan (el : JsonElement) : SpanRec =
  let traceId =
    match el.TryGetProperty "traceId" with
    | true, v -> jsonHex v
    | _ -> ""
  let spanId =
    match el.TryGetProperty "spanId" with
    | true, v -> jsonHex v
    | _ -> ""
  let parentId =
    match el.TryGetProperty "parentSpanId" with
    | true, v -> jsonHex v
    | _ -> ""
  let name =
    match el.TryGetProperty "name" with
    | true, n when n.ValueKind = JsonValueKind.String -> n.GetString()
    | _ -> ""
  { traceId = traceId
    spanId  = spanId
    parentId = parentId
    name    = name
    kind    = jsonIntField el "kind"
    startNs = jsonUint64Field el "startTimeUnixNano"
    endNs   = jsonUint64Field el "endTimeUnixNano"
    status  = jsonStatusCode el
    attrs   = jsonAttrs el }

let private jsonScopeSpans (el : JsonElement) : ScopeSpansRec =
  let spans =
    match el.TryGetProperty "spans" with
    | true, s when s.ValueKind = JsonValueKind.Array ->
      s.EnumerateArray() |> Seq.map jsonSpan |> Seq.toArray
    | _ -> [||]
  { spans = spans }

let private jsonResourceSpans (el : JsonElement) : ResourceSpansRec =
  let resAttrs =
    match el.TryGetProperty "resource" with
    | true, r -> jsonAttrs r
    | _ -> [||]
  let scopes =
    match el.TryGetProperty "scopeSpans" with
    | true, ss when ss.ValueKind = JsonValueKind.Array ->
      ss.EnumerateArray() |> Seq.map jsonScopeSpans |> Seq.toArray
    | _ -> [||]
  { resource = resAttrs; scopes = scopes }

let private decodeExportTraceJson (raw : byte[]) : ResourceSpansRec[] =
  use doc = JsonDocument.Parse(ReadOnlyMemory<byte>(raw))
  let root = doc.RootElement
  match root.TryGetProperty "resourceSpans" with
  | true, rs when rs.ValueKind = JsonValueKind.Array ->
    rs.EnumerateArray() |> Seq.map jsonResourceSpans |> Seq.toArray
  | _ -> [||]

/// JSON twin of `decodeSpans`. Phase 16 tracers + Step Functions
/// `OtlpHttpSpanSink` POST OTLP/JSON to `/v1/traces`; this is the
/// entry point that turns those payloads into the public Span model.
let decodeSpansJson (raw : byte[]) : PulseBoard.Spans.Span[] * int =
  liftSpans (decodeExportTraceJson raw)

let private isJsonContent (ctx : HttpContext) =
  ctx.request.headers
  |> Seq.exists (fun (k, v) ->
       String.Equals(k, "content-type", StringComparison.OrdinalIgnoreCase)
       && v.IndexOf("application/json", StringComparison.OrdinalIgnoreCase) >= 0)

let private headerValue (name : string) (ctx : HttpContext) : string option =
  ctx.request.headers
  |> Seq.tryPick (fun (k, v) ->
       if String.Equals(k, name, StringComparison.OrdinalIgnoreCase)
       then Some v else None)

let private decodeRequestBody (ctx : HttpContext) (raw : byte[]) : byte[] =
  let enc =
    headerValue "content-encoding" ctx
    |> Option.defaultValue ""
    |> fun v -> v.Trim().ToLowerInvariant()
  if enc.Contains("gzip") then
    use input = new MemoryStream(raw)
    use gz = new GZipStream(input, CompressionMode.Decompress)
    use output = new MemoryStream()
    gz.CopyTo(output)
    output.ToArray()
  else
    raw

// ---------- Handlers ----------

/// POST /v1/metrics — OTLP/HTTP metrics. Body is a protobuf
/// `ExportMetricsServiceRequest` (Content-Type: application/x-protobuf)
/// or Proto3 JSON (Content-Type: application/json). We map every
/// NumberDataPoint of every Gauge / Sum metric to a `MetricSample`,
/// naming series with resource attrs ∪ scope name ∪ point attrs.
/// Histograms / summaries are silently ignored for now.
let metrics (storage : IStorageClient)
            (quotas : IngestQuotas option) : WebPart =
  fun ctx -> async {
    PulseBoard.HeartbeatClient.bump ()
    try
      let raw = decodeRequestBody ctx ctx.request.rawForm
      if isNull raw || raw.Length = 0 then
        return! BAD_REQUEST """{"error":"empty body"}""" ctx
      else
      let resourceMetrics =
        if isJsonContent ctx then
          decodeExportMetricsJson raw
        else
          use ms = new MemoryStream(raw)
          let input = new CodedInputStream(ms)
          decodeExportMetricsRequest input
      let tenantId =
        PulseBoard.Rbac.tryGetTenant ctx
        |> Option.map (fun t -> t.tenant.id)
      let samples = ResizeArray<MetricSample>()
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
                  samples.Add { seriesName = name; tsMs = tsMs; value = p.value }
      let tid = match tenantId with Some (TenantId s) -> s | None -> ""
      do! storage.WriteMetricSamples(tid, samples)
      return! (OK partialSuccessBody >=> okHeaders samples.Count) ctx
    with ex ->
      return!
        BAD_REQUEST
          (sprintf """{"error":%s}"""
             (JsonSerializer.Serialize ex.Message)) ctx
  }

/// POST /v1/logs — OTLP/HTTP logs. Body length charged against the
/// tenant's LogBytes bucket before parsing; over-quota → 429.
let logs (storage : IStorageClient)
         (quotas : IngestQuotas option) : WebPart =
  fun ctx -> async {
    PulseBoard.HeartbeatClient.bump ()
    try
      let raw = decodeRequestBody ctx ctx.request.rawForm
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
        let entries = ResizeArray<LogEntry>()
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
              entries.Add { ts = tsMs; service = service; level = level; message = message }
        let tid = match tenantId with Some (TenantId s) -> s | None -> ""
        do! storage.WriteLogs(tid, entries)
        return! (OK partialSuccessBody >=> okHeaders entries.Count) ctx
    with ex ->
      return!
        BAD_REQUEST
          (sprintf """{"error":%s}"""
             (JsonSerializer.Serialize ex.Message)) ctx
  }

/// POST /v1/traces — accepted, counted, persisted into the in-memory
/// span store (for /api/traces + /api/servicemap), and optionally
/// forwarded to Tempo via `rawTrace`. The count always flows through
/// `IStorageClient.IncTraceCount` for billing. Accepts both
/// application/x-protobuf (Phase 16 tracers) and application/json
/// (Step Functions Firehose translator). Tempo raw-forward only fires
/// for protobuf because `IRawTraceBackend` is protobuf-only.
let traces (storage : IStorageClient)
           (rawTrace : PulseBoard.CloudBackends.IRawTraceBackend option)
           (spanStore : PulseBoard.Spans.ISpanStore option) : WebPart =
  fun ctx -> async {
    PulseBoard.HeartbeatClient.bump ()
    try
      let raw = decodeRequestBody ctx ctx.request.rawForm
      if isNull raw || raw.Length = 0 then
        return! BAD_REQUEST """{"error":"empty body"}""" ctx
      else
      let tenantId =
        PulseBoard.Rbac.tryGetTenant ctx
        |> Option.map (fun t -> t.tenant.id)
      let tid = match tenantId with Some (TenantId s) -> s | None -> ""
      let json = isJsonContent ctx
      // Single decode pass that produces both structured spans and a
      // raw count. If decode fails we still want to record the count
      // (best-effort), so fall back to the legacy protobuf counter.
      let spans, n =
        try
          if json then decodeSpansJson raw
          else decodeSpans raw
        with _ ->
          if json then [||], 0
          else
            use ms = new MemoryStream(raw)
            let input = new CodedInputStream(ms)
            [||], countSpans input
      do! storage.IncTraceCount(tid, n)
      match spanStore with
      | Some store when spans.Length > 0 ->
        let tidVal = match tenantId with Some t -> t | None -> TenantId "__local__"
        store.Ingest(tidVal, spans)
      | _ -> ()
      // Tempo raw-forward is protobuf-only — `IRawTraceBackend` does
      // not accept JSON payloads. JSON spans land in the in-memory
      // span store above; long-term storage waits for the JSON path
      // on the raw-trace backend.
      match rawTrace with
      | Some rt when not json ->
        try rt.IngestOtlpProtobuf(tid, raw) with _ -> ()
      | _ -> ()
      return! (OK partialSuccessBody >=> okHeaders n) ctx
    with ex ->
      return!
        BAD_REQUEST
          (sprintf """{"error":%s}"""
             (JsonSerializer.Serialize ex.Message)) ctx
  }

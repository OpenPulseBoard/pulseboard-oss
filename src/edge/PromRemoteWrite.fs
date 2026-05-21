module PulseBoard.PromRemoteWrite

open System
open System.IO
open System.Text
open System.Text.Json
open Suave
open Suave.Operators
open Suave.Successful
open Suave.RequestErrors
open Google.Protobuf
open Snappier
open PulseBoard.TimeSeries
open PulseBoard.Hub
open PulseBoard.Tenancy
open PulseBoard.Quotas
open PulseBoard.Audit
open PulseBoard.Ingest

// Prometheus remote_write 1.0 receiver. The wire format is a snappy-
// framed protobuf payload defined in prompb/remote.proto + types.proto:
//
//   message WriteRequest { repeated TimeSeries timeseries = 1; }
//   message TimeSeries   { repeated Label  labels  = 1;
//                          repeated Sample samples = 2;
//                          // exemplars=3, histograms=4 (ignored) }
//   message Label        { string name = 1; string value = 2; }
//   message Sample       { double value = 1; int64 timestamp = 2; }
//
// We hand-decode against `Google.Protobuf.CodedInputStream` because the
// schema is small (4 messages) and pulling in `Grpc.Tools` codegen would
// force a separate C# project just for these types. OTLP, whose schema is
// large, will earn that separation in a follow-up.

[<Struct>] type private Label  = { name : string; value : string }
[<Struct>] type private Sample = { value : float; tsMs : int64 }
type private Series = { labels : Label[]; samples : Sample[] }

let private fieldOf (tag : uint32) = int (tag >>> 3)

let private decodeLabel (bytes : ByteString) : Label =
  let input = new CodedInputStream(bytes.ToByteArray())
  let mutable name  = ""
  let mutable value = ""
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> name  <- input.ReadString()
    | 2 -> value <- input.ReadString()
    | _ -> input.SkipLastField()
  { name = name; value = value }

let private decodeSample (bytes : ByteString) : Sample =
  let input = new CodedInputStream(bytes.ToByteArray())
  let mutable value = 0.0
  let mutable ts    = 0L
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> value <- input.ReadDouble()
    | 2 -> ts    <- input.ReadInt64()
    | _ -> input.SkipLastField()
  { value = value; tsMs = ts }

let private decodeSeries (bytes : ByteString) : Series =
  let input = new CodedInputStream(bytes.ToByteArray())
  let labels  = ResizeArray<Label>()
  let samples = ResizeArray<Sample>()
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> labels.Add(decodeLabel (input.ReadBytes()))
    | 2 -> samples.Add(decodeSample (input.ReadBytes()))
    | _ -> input.SkipLastField()
  { labels = labels.ToArray(); samples = samples.ToArray() }

let private decodeWriteRequest (input : CodedInputStream) : Series[] =
  let series = ResizeArray<Series>()
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> series.Add(decodeSeries (input.ReadBytes()))
    | _ -> input.SkipLastField()
  series.ToArray()

let private nameLabel = "__name__"

/// Canonical Prometheus series name: `<__name__>{l1="v1",l2="v2"}` with
/// labels sorted by name (excluding `__name__`). Backslash, double-quote
/// and newline in label values are escaped per Prometheus convention.
let private seriesName (labels : Label[]) : string =
  let mutable metric = ""
  let others = ResizeArray<Label>(labels.Length)
  for l in labels do
    if l.name = nameLabel then metric <- l.value
    else others.Add l
  if others.Count = 0 then metric
  else
    let sorted = others.ToArray()
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

/// POST /api/v1/write — Prometheus remote_write 1.0. Body is snappy-
/// compressed protobuf `prometheus.WriteRequest`. NaN samples (Prom
/// "stale marker" convention) are silently dropped. Cardinality
/// admission is per fully-qualified series name (metric + sorted
/// labelset); over-cap series have all their samples in this request
/// dropped and counted, mirroring the JSON ingest path.
let handler (store : MetricStore) (hub : Broadcaster)
            (quotas : IngestQuotas option) : WebPart =
  fun ctx -> async {
    try
      let raw = ctx.request.rawForm
      if isNull raw || raw.Length = 0 then
        return! BAD_REQUEST """{"error":"empty body"}""" ctx
      else
      let decompressed =
        try Snappy.DecompressToArray(ReadOnlySpan(raw))
        with _ ->
          // Some agents (Mimir-flavoured) may post un-snappy'd. Try the
          // raw body as protobuf before giving up.
          raw
      use ms = new MemoryStream(decompressed)
      let input = new CodedInputStream(ms)
      let series = decodeWriteRequest input
      let tenantId =
        PulseBoard.Rbac.tryGetTenant ctx
        |> Option.map (fun t -> t.tenant.id)
      let mutable acceptedSamples = 0
      let mutable rejectedSamples = 0
      let mutable rejectedCap     = 0
      for s in series do
        let name = seriesName s.labels
        if name.Length > 0 then
          let admit =
            match quotas, tenantId with
            | Some q, Some tid ->
              match q.limiter.TryAdmitSeries(tid, name) with
              | CardinalityResult.Ok -> true
              | CardinalityResult.Rejected cap ->
                rejectedSamples <- rejectedSamples + s.samples.Length
                rejectedCap     <- cap
                auditDeny q ctx "quota.cardinality"
                  (sprintf "series=%s cap=%d" name cap)
                false
            | _ -> true
          if admit then
            for sample in s.samples do
              if not (Double.IsNaN sample.value) then
                let p : Point = { ts = sample.tsMs; value = sample.value }
                store.Record(name, p)
                publishMetric hub name p
                acceptedSamples <- acceptedSamples + 1
      let body =
        if rejectedSamples > 0 then
          sprintf
            """{"acceptedSamples":%d,"rejectedCardinality":%d,"cap":%d,"series":%d}"""
            acceptedSamples rejectedSamples rejectedCap series.Length
        else
          sprintf """{"acceptedSamples":%d,"series":%d}"""
            acceptedSamples series.Length
      // Prometheus accepts 200 or 204. We return 200 + small JSON body
      // for parity with /ingest/metrics and easier curl smoke tests.
      return! (OK body >=> Writers.setMimeType "application/json") ctx
    with ex ->
      return!
        BAD_REQUEST
          (sprintf """{"error":%s}"""
             (JsonSerializer.Serialize ex.Message)) ctx
  }

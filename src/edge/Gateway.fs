module PulseBoard.Gateway

open System
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open Google.Protobuf
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open Suave.ServerErrors
open PulseBoard.TimeSeries
open PulseBoard.Hub
open PulseBoard.Storage

// Edge / storage split (PLAN.md Phase 2 step 6).
//
// `IStorageClient` is the seam every ingest path now writes through.
// * `InProcessStorageClient` keeps the monolith behaviour (writes
//   straight into MetricStore/LogStore/Hub).
// * `HttpStorageClient` POSTs to `/_internal/v1/{metrics,logs,trace-count}`
//   on a remote storage tier, framed as hand-rolled protobuf and
//   authenticated by `X-Pulse-Signature: hex(HMAC_SHA256(secret, body))`.
//
// The storage-tier server is `internalWebPart`, which decodes the same
// protobuf, verifies the signature, and forwards into its own
// `InProcessStorageClient`.
//
// The internal protocol is intentionally small (3 message types,
// hand-encoded) so we don't drag in Grpc.Tools codegen. Receiver-side
// decode mirrors the pattern already used by `PromRemoteWrite` /
// `Otlp` / `LokiPush`.

// -- Wire types --------------------------------------------------------------

[<Struct>]
type MetricSample =
  { seriesName : string; tsMs : int64; value : float }

/// Storage abstraction shared by every receiver. All methods are
/// async-fire-but-await: receivers `do!` them so backpressure / failure
/// from the storage tier surfaces back to the ingest HTTP caller.
type IStorageClient =
  abstract WriteMetricSamples :
    tenantId:string * samples:MetricSample seq -> Async<unit>
  abstract WriteLogs          :
    tenantId:string * entries:LogEntry seq    -> Async<unit>
  abstract IncTraceCount      :
    tenantId:string * count:int               -> Async<unit>


// -- Hand-encoded protobuf ---------------------------------------------------
// Field numbers (kept short on purpose; we control both sides of the wire):
//
//   WriteMetricsReq:
//     1 string tenant_id
//     2 repeated MetricSample samples
//   MetricSample:
//     1 string  series_name
//     2 sfixed64 ts_ms
//     3 double  value
//
//   WriteLogsReq:
//     1 string tenant_id
//     2 repeated LogEntryMsg entries
//   LogEntryMsg:
//     1 sfixed64 ts_ms
//     2 string service
//     3 string level
//     4 string message
//
//   IncTraceReq:
//     1 string tenant_id
//     2 int32  count

let private writeStringField (out : CodedOutputStream) (field : int) (s : string) =
  out.WriteTag(field, WireFormat.WireType.LengthDelimited)
  out.WriteString(if isNull s then "" else s)

let private writeSFixed64Field (out : CodedOutputStream) (field : int) (v : int64) =
  out.WriteTag(field, WireFormat.WireType.Fixed64)
  out.WriteSFixed64(v)

let private writeDoubleField (out : CodedOutputStream) (field : int) (v : float) =
  out.WriteTag(field, WireFormat.WireType.Fixed64)
  out.WriteDouble(v)

let private writeInt32Field (out : CodedOutputStream) (field : int) (v : int) =
  out.WriteTag(field, WireFormat.WireType.Varint)
  out.WriteInt32(v)

/// Wire format for an embedded message field is identical to a `bytes`
/// field: tag | varint(len) | payload. `WriteBytes` handles the
/// length-prefix for us.
let private writeNestedMessage (out : CodedOutputStream)
                               (field : int) (payload : byte[]) =
  out.WriteTag(field, WireFormat.WireType.LengthDelimited)
  out.WriteBytes(ByteString.CopyFrom payload)

let private encodeSample (s : MetricSample) : byte[] =
  use ms = new MemoryStream()
  let out = new CodedOutputStream(ms)
  writeStringField   out 1 s.seriesName
  writeSFixed64Field out 2 s.tsMs
  writeDoubleField   out 3 s.value
  out.Flush()
  ms.ToArray()

let private encodeLog (e : LogEntry) : byte[] =
  use ms = new MemoryStream()
  let out = new CodedOutputStream(ms)
  writeSFixed64Field out 1 e.ts
  writeStringField   out 2 e.service
  writeStringField   out 3 e.level
  writeStringField   out 4 e.message
  out.Flush()
  ms.ToArray()

let encodeWriteMetricsReq (tenantId : string) (samples : MetricSample seq) : byte[] =
  use ms = new MemoryStream()
  let out = new CodedOutputStream(ms)
  writeStringField out 1 tenantId
  for s in samples do
    writeNestedMessage out 2 (encodeSample s)
  out.Flush()
  ms.ToArray()

let encodeWriteLogsReq (tenantId : string) (entries : LogEntry seq) : byte[] =
  use ms = new MemoryStream()
  let out = new CodedOutputStream(ms)
  writeStringField out 1 tenantId
  for e in entries do
    writeNestedMessage out 2 (encodeLog e)
  out.Flush()
  ms.ToArray()

let encodeIncTraceReq (tenantId : string) (count : int) : byte[] =
  use ms = new MemoryStream()
  let out = new CodedOutputStream(ms)
  writeStringField out 1 tenantId
  writeInt32Field  out 2 count
  out.Flush()
  ms.ToArray()

let private fieldOf (tag : uint32) = int (tag >>> 3)

let private decodeSampleBytes (bytes : ByteString) : MetricSample =
  let input = new CodedInputStream(bytes.ToByteArray())
  let mutable name = ""
  let mutable ts = 0L
  let mutable v = 0.0
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> name <- input.ReadString()
    | 2 -> ts   <- input.ReadSFixed64()
    | 3 -> v    <- input.ReadDouble()
    | _ -> input.SkipLastField()
  { seriesName = name; tsMs = ts; value = v }

let private decodeLogBytes (bytes : ByteString) : LogEntry =
  let input = new CodedInputStream(bytes.ToByteArray())
  let mutable ts = 0L
  let mutable svc = ""
  let mutable lvl = ""
  let mutable msg = ""
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> ts  <- input.ReadSFixed64()
    | 2 -> svc <- input.ReadString()
    | 3 -> lvl <- input.ReadString()
    | 4 -> msg <- input.ReadString()
    | _ -> input.SkipLastField()
  { ts = ts; service = svc; level = lvl; message = msg }

let decodeWriteMetricsReq (body : byte[]) : string * MetricSample[] =
  use ms = new MemoryStream(body)
  let input = new CodedInputStream(ms)
  let mutable tid = ""
  let samples = ResizeArray<MetricSample>()
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> tid <- input.ReadString()
    | 2 -> samples.Add(decodeSampleBytes (input.ReadBytes()))
    | _ -> input.SkipLastField()
  tid, samples.ToArray()

let decodeWriteLogsReq (body : byte[]) : string * LogEntry[] =
  use ms = new MemoryStream(body)
  let input = new CodedInputStream(ms)
  let mutable tid = ""
  let entries = ResizeArray<LogEntry>()
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> tid <- input.ReadString()
    | 2 -> entries.Add(decodeLogBytes (input.ReadBytes()))
    | _ -> input.SkipLastField()
  tid, entries.ToArray()

let decodeIncTraceReq (body : byte[]) : string * int =
  use ms = new MemoryStream(body)
  let input = new CodedInputStream(ms)
  let mutable tid = ""
  let mutable n = 0
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> tid <- input.ReadString()
    | 2 -> n   <- input.ReadInt32()
    | _ -> input.SkipLastField()
  tid, n


// -- HMAC --------------------------------------------------------------------
// `X-Pulse-Signature: hex(HMAC_SHA256(secret, body))`. Constant-time
// compare on verify. No replay protection in this iteration — the
// internal hop is expected to ride a private network and secret rotation
// is the security boundary. Reject if signature header is missing.

let signBody (secret : byte[]) (body : byte[]) : string =
  use h = new HMACSHA256(secret)
  Convert.ToHexString(h.ComputeHash(body)).ToLowerInvariant()

let constantTimeEquals (a : string) (b : string) : bool =
  if a.Length <> b.Length then false
  else
    let mutable diff = 0
    for i in 0 .. a.Length - 1 do
      diff <- diff ||| (int a.[i] ^^^ int b.[i])
    diff = 0

let verifySignature (secret : byte[]) (body : byte[]) (provided : string) : bool =
  constantTimeEquals (signBody secret body) (provided.ToLowerInvariant())

/// Parse a hex-encoded secret from CLI / env into bytes. Accepts
/// upper/lower hex. Throws on invalid hex; caller handles.
let secretFromHex (s : string) : byte[] = Convert.FromHexString s

/// Generate a fresh random 32-byte secret, hex-encoded — handy for the
/// startup banner so an operator can copy the same string into both
/// processes.
let generateSecretHex () : string =
  let buf = Array.zeroCreate 32
  use rng = RandomNumberGenerator.Create()
  rng.GetBytes buf
  Convert.ToHexString(buf).ToLowerInvariant()


// -- In-process impl ---------------------------------------------------------

let private publishMetric (hub : Broadcaster) (name : string) (p : Point) =
  let json =
    sprintf """{"type":"metric","name":%s,"ts":%d,"value":%s}"""
      (JsonSerializer.Serialize name) p.ts
      (p.value.ToString(System.Globalization.CultureInfo.InvariantCulture))
  hub.Publish json

let private publishLog (hub : Broadcaster) (e : LogEntry) =
  let json =
    sprintf """{"type":"log","ts":%d,"service":%s,"level":%s,"message":%s}"""
      e.ts (JsonSerializer.Serialize e.service)
      (JsonSerializer.Serialize e.level) (JsonSerializer.Serialize e.message)
  hub.Publish json

type InProcessStorageClient(metrics : IMetricBackend,
                            logs    : ILogBackend,
                            traces  : ITraceBackend,
                            hub     : Broadcaster) =
  interface IStorageClient with
    member _.WriteMetricSamples(tid, samples) = async {
      for s in samples do
        let p : Point = { ts = s.tsMs; value = s.value }
        match metrics.Record(tid, s.seriesName, p) with
        | WriteOutcome.Accepted ->
          publishMetric hub s.seriesName p
        | WriteOutcome.DroppedCardinality _ ->
          // Silent drop — receivers see the request as accepted, the
          // tenant's drop counter is incremented on the backend, and
          // the admin cardinality endpoint exposes the running total.
          ()
    }
    member _.WriteLogs(tid, entries) = async {
      for e in entries do
        logs.Add(tid, e)
        publishLog hub e
    }
    member _.IncTraceCount(tid, count) = async {
      traces.IncCount(tid, count)
    }


// -- HTTP impl ---------------------------------------------------------------
// Retry on transient (network / 5xx) with capped exponential backoff;
// give up after `maxRetries`. On final failure, raises the underlying
// exception so the receiver returns 500 to the original caller.

type HttpStorageClient(endpoint : string, secret : byte[],
                       ?http : HttpClient, ?maxRetries : int) =
  let client =
    match http with
    | Some h -> h
    | None ->
      let h = new HttpClient()
      h.Timeout <- TimeSpan.FromSeconds 15.0
      h
  let endpoint = endpoint.TrimEnd '/'
  let maxRetries = defaultArg maxRetries 3

  let post (pathSeg : string) (body : byte[]) : Async<unit> = async {
    let url = endpoint + pathSeg
    let sg  = signBody secret body
    let mutable attempt = 0
    let mutable lastErr : exn option = None
    let mutable finished = false
    while not finished && attempt < maxRetries do
      attempt <- attempt + 1
      try
        use req = new HttpRequestMessage(HttpMethod.Post, url)
        let content = new ByteArrayContent(body)
        content.Headers.ContentType <- MediaTypeHeaderValue("application/x-protobuf")
        req.Content <- content
        req.Headers.TryAddWithoutValidation("X-Pulse-Signature", sg) |> ignore
        let! resp = client.SendAsync(req) |> Async.AwaitTask
        if int resp.StatusCode >= 500 then
          let! bodyStr = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
          lastErr <- Some (exn (sprintf "storage %d: %s" (int resp.StatusCode) bodyStr))
        elif not resp.IsSuccessStatusCode then
          // 4xx — caller error (bad signature etc). Do not retry.
          let! bodyStr = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
          raise (exn (sprintf "storage %d: %s" (int resp.StatusCode) bodyStr))
        else
          finished <- true
      with ex ->
        lastErr <- Some ex
      if not finished && attempt < maxRetries then
        let backoffMs = 100 <<< (attempt - 1)   // 100, 200, 400
        do! Async.Sleep backoffMs
    if not finished then
      match lastErr with
      | Some ex -> raise ex
      | None -> raise (exn "storage write failed")
  }

  interface IStorageClient with
    member _.WriteMetricSamples(tenantId, samples) = async {
      let arr = samples |> Seq.toArray
      if arr.Length = 0 then return ()
      else
        let body = encodeWriteMetricsReq tenantId arr
        return! post "/_internal/v1/metrics" body
    }
    member _.WriteLogs(tenantId, entries) = async {
      let arr = entries |> Seq.toArray
      if arr.Length = 0 then return ()
      else
        let body = encodeWriteLogsReq tenantId arr
        return! post "/_internal/v1/logs" body
    }
    member _.IncTraceCount(tenantId, count) = async {
      if count = 0 then return ()
      else
        let body = encodeIncTraceReq tenantId count
        return! post "/_internal/v1/trace-count" body
    }


// -- Storage-side WebPart ----------------------------------------------------

let internalWebPart (storage : IStorageClient) (secret : byte[]) : WebPart =
  let readBody (ctx : HttpContext) = ctx.request.rawForm
  let verifyReq (ctx : HttpContext) (body : byte[]) =
    let provided =
      ctx.request.headers
      |> Seq.tryFind (fun (k, _) ->
          String.Equals(k, "X-Pulse-Signature", StringComparison.OrdinalIgnoreCase))
      |> Option.map snd
      |> Option.defaultValue ""
    if provided.Length = 0 then false
    else verifySignature secret body provided
  let safeJson (s : string) = JsonSerializer.Serialize (if isNull s then "" else s)
  let handleMetrics : WebPart =
    fun ctx -> async {
      let body = readBody ctx
      if isNull body || body.Length = 0 then
        return! BAD_REQUEST """{"error":"empty body"}""" ctx
      elif not (verifyReq ctx body) then
        return! UNAUTHORIZED """{"error":"bad signature"}""" ctx
      else
        try
          let tid, samples = decodeWriteMetricsReq body
          do! storage.WriteMetricSamples(tid, samples)
          return! (OK (sprintf """{"accepted":%d}""" samples.Length)
                   >=> Writers.setMimeType "application/json") ctx
        with ex ->
          return!
            INTERNAL_ERROR
              (sprintf """{"error":%s}""" (safeJson ex.Message)) ctx
    }
  let handleLogs : WebPart =
    fun ctx -> async {
      let body = readBody ctx
      if isNull body || body.Length = 0 then
        return! BAD_REQUEST """{"error":"empty body"}""" ctx
      elif not (verifyReq ctx body) then
        return! UNAUTHORIZED """{"error":"bad signature"}""" ctx
      else
        try
          let tid, entries = decodeWriteLogsReq body
          do! storage.WriteLogs(tid, entries)
          return! (OK (sprintf """{"accepted":%d}""" entries.Length)
                   >=> Writers.setMimeType "application/json") ctx
        with ex ->
          return!
            INTERNAL_ERROR
              (sprintf """{"error":%s}""" (safeJson ex.Message)) ctx
    }
  let handleTrace : WebPart =
    fun ctx -> async {
      let body = readBody ctx
      if isNull body || body.Length = 0 then
        return! BAD_REQUEST """{"error":"empty body"}""" ctx
      elif not (verifyReq ctx body) then
        return! UNAUTHORIZED """{"error":"bad signature"}""" ctx
      else
        try
          let tid, n = decodeIncTraceReq body
          do! storage.IncTraceCount(tid, n)
          return! (OK """{"ok":true}""" >=> Writers.setMimeType "application/json") ctx
        with ex ->
          return!
            INTERNAL_ERROR
              (sprintf """{"error":%s}""" (safeJson ex.Message)) ctx
    }
  POST >=> choose [
    path "/_internal/v1/metrics"     >=> handleMetrics
    path "/_internal/v1/logs"        >=> handleLogs
    path "/_internal/v1/trace-count" >=> handleTrace
  ]

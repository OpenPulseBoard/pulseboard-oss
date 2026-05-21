module PulseBoard.CloudBackends

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Google.Protobuf
open Snappier
open PulseBoard.TimeSeries
open PulseBoard.Storage

// Cloud backends for PLAN.md Phase 3 step 1: HTTP clients that
// implement `IMetricBackend` / `ILogBackend` / `ITraceBackend` so
// PulseBoard can fan out to Mimir (Prometheus remote_write 1.0),
// Loki (`/loki/api/v1/push`, JSON encoding), and Tempo (OTLP/HTTP
// passthrough on `/v1/traces`).
//
// Design constraints these clients share:
//
//   * `Record` / `Add` are SYNC on the interface; cloud writes are not.
//     Each backend therefore owns a bounded in-memory queue and a
//     dedicated background flush task. Overflow drops the sample
//     and bumps an internal counter (no log spam in hot loops).
//
//   * Multi-tenancy is forwarded via an HTTP header (default
//     `X-Scope-OrgID`, matching Mimir / Loki / Tempo's "org" model).
//     A `Bearer` token can be added for hosted offerings (Grafana
//     Cloud, AWS Managed Prometheus, etc).
//
//   * Read-path methods (`Names`, `Get`, `GetSince`, `Tail`) return
//     empty / zero today. PulseBoard's built-in /api/metrics and
//     /api/logs endpoints continue to serve from the in-process
//     ring; PromQL / LogQL proxying through the cloud backend will
//     land alongside Query.fs decoupling in a follow-up.

// ---------------------------------------------------------------------------
// Shared helpers
// ---------------------------------------------------------------------------

let private defaultFlushMs   = 500
let private defaultMaxBatch  = 1000
let private defaultQueueCap  = 100_000

let private sharedClient =
  // One process-wide HttpClient; pipelined connections per host.
  let h = new HttpClientHandler()
  h.AutomaticDecompression <- Net.DecompressionMethods.GZip ||| Net.DecompressionMethods.Deflate
  new HttpClient(h, Timeout = TimeSpan.FromSeconds 30.0)

let private buildRequest (method' : HttpMethod)
                         (url : string)
                         (orgIdHeader : string option)
                         (tenantId : string)
                         (bearer : string option)
                         (contentType : string)
                         (body : byte[]) : HttpRequestMessage =
  let req = new HttpRequestMessage(method', url)
  req.Content <- new ByteArrayContent(body)
  req.Content.Headers.ContentType <- MediaTypeHeaderValue(contentType)
  match orgIdHeader with
  | Some h when not (String.IsNullOrWhiteSpace tenantId) ->
    req.Headers.TryAddWithoutValidation(h, tenantId) |> ignore
  | _ -> ()
  match bearer with
  | Some t -> req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", t)
  | None   -> ()
  req

// ---------------------------------------------------------------------------
// Mimir — Prometheus remote_write 1.0 encoder
// ---------------------------------------------------------------------------
//
//   message WriteRequest { repeated TimeSeries timeseries = 1; }
//   message TimeSeries   { repeated Label labels = 1;
//                          repeated Sample samples = 2; }
//   message Label        { string name = 1;  string value = 2; }
//   message Sample       { double value = 1; int64 timestamp = 2; }

[<Struct>] type internal PromLabel  = { name : string; value : string }
[<Struct>] type internal PromSample = { tsMs : int64; value : float }

/// Parse PulseBoard's canonical series name (`cpu{host="a",region="b"}`)
/// back into `__name__` + sorted label pairs. Plain names without `{...}`
/// become a single `__name__` label.
let private parseSeriesName (full : string) : PromLabel[] =
  if isNull full then [||]
  else
    let brace = full.IndexOf '{'
    if brace < 0 then
      [| { name = "__name__"; value = full } |]
    else
      let metric = full.Substring(0, brace).Trim()
      let inner =
        let s = full.Substring(brace + 1)
        let s = if s.EndsWith "}" then s.Substring(0, s.Length - 1) else s
        s
      // Tokenise `k="v",k2="v2"` honouring backslash escapes inside values.
      let labels = ResizeArray<PromLabel>()
      let sb = StringBuilder()
      let mutable i = 0
      while i < inner.Length do
        // Parse name up to '='.
        sb.Clear() |> ignore
        while i < inner.Length && inner.[i] <> '=' do
          sb.Append inner.[i] |> ignore
          i <- i + 1
        let name = sb.ToString().Trim()
        if i < inner.Length then i <- i + 1     // skip '='
        // Optional opening quote.
        if i < inner.Length && inner.[i] = '"' then i <- i + 1
        // Parse value with backslash escapes, terminating on unescaped '"'.
        sb.Clear() |> ignore
        let mutable closed = false
        while i < inner.Length && not closed do
          let c = inner.[i]
          if c = '\\' && i + 1 < inner.Length then
            let n = inner.[i + 1]
            let decoded =
              match n with
              | '\\' -> '\\'
              | '"'  -> '"'
              | 'n'  -> '\n'
              | other -> other
            sb.Append decoded |> ignore
            i <- i + 2
          elif c = '"' then
            closed <- true
            i <- i + 1
          else
            sb.Append c |> ignore
            i <- i + 1
        let value = sb.ToString()
        if name.Length > 0 then
          labels.Add { name = name; value = value }
        // Skip comma + any whitespace.
        while i < inner.Length && (inner.[i] = ',' || Char.IsWhiteSpace inner.[i]) do
          i <- i + 1
      labels.Insert(0, { name = "__name__"; value = metric })
      labels.ToArray()

let private encodeLabel (l : PromLabel) : byte[] =
  use ms = new MemoryStream()
  let out = new CodedOutputStream(ms)
  out.WriteTag(1, WireFormat.WireType.LengthDelimited); out.WriteString(l.name)
  out.WriteTag(2, WireFormat.WireType.LengthDelimited); out.WriteString(l.value)
  out.Flush(); ms.ToArray()

let private encodePromSample (s : PromSample) : byte[] =
  use ms = new MemoryStream()
  let out = new CodedOutputStream(ms)
  out.WriteTag(1, WireFormat.WireType.Fixed64); out.WriteDouble(s.value)
  out.WriteTag(2, WireFormat.WireType.Varint);  out.WriteInt64(s.tsMs)
  out.Flush(); ms.ToArray()

let private encodeTimeSeries (labels : PromLabel[]) (samples : PromSample[]) : byte[] =
  use ms = new MemoryStream()
  let out = new CodedOutputStream(ms)
  for l in labels do
    out.WriteTag(1, WireFormat.WireType.LengthDelimited)
    out.WriteBytes(ByteString.CopyFrom(encodeLabel l))
  for s in samples do
    out.WriteTag(2, WireFormat.WireType.LengthDelimited)
    out.WriteBytes(ByteString.CopyFrom(encodePromSample s))
  out.Flush(); ms.ToArray()

/// Encode and snappy-compress a WriteRequest. `series` groups samples
/// by canonical series name (one TimeSeries per group).
let internal encodeRemoteWrite (series : (string * PromSample[])[]) : byte[] =
  use ms = new MemoryStream()
  let out = new CodedOutputStream(ms)
  for (name, samples) in series do
    if samples.Length > 0 then
      let labels = parseSeriesName name
      out.WriteTag(1, WireFormat.WireType.LengthDelimited)
      out.WriteBytes(ByteString.CopyFrom(encodeTimeSeries labels samples))
  out.Flush()
  let raw = ms.ToArray()
  Snappy.CompressToArray(ReadOnlySpan(raw))

// ---------------------------------------------------------------------------
// Background-flush queue plumbing
// ---------------------------------------------------------------------------

type private Pending<'T> = { tenantId : string; payload : 'T }

/// Bounded SPSC-ish queue with periodic + size-triggered flush. Drops
/// on overflow and surfaces the count via `OverflowDropped`.
type private FlushPump<'T>(maxQueue : int,
                           flushMs  : int,
                           maxBatch : int,
                           flush    : (string * 'T[])[] -> Async<unit>) =
  let queue   = ConcurrentQueue<Pending<'T>>()
  let mutable size = 0
  let mutable overflowDropped = 0L
  let mutable flushErrors     = 0L
  let cts = new CancellationTokenSource()

  let drainBatch () : (string * 'T[])[] =
    // Pull up to maxBatch items grouped by tenant id.
    let buckets = Dictionary<string, ResizeArray<'T>>()
    let mutable n = 0
    let mutable ok = true
    while ok && n < maxBatch do
      match queue.TryDequeue() with
      | true, item ->
        Interlocked.Decrement(&size) |> ignore
        let arr =
          match buckets.TryGetValue item.tenantId with
          | true, a -> a
          | _ ->
            let a = ResizeArray<'T>()
            buckets.[item.tenantId] <- a
            a
        arr.Add item.payload
        n <- n + 1
      | _ -> ok <- false
    [|
      for KeyValue(tid, arr) in buckets ->
        tid, arr.ToArray()
    |]

  let loop = async {
    while not cts.IsCancellationRequested do
      try
        do! Async.Sleep flushMs
        if size > 0 then
          let batch = drainBatch ()
          if batch.Length > 0 then
            try do! flush batch
            with _ -> Interlocked.Increment(&flushErrors) |> ignore
      with
      | :? OperationCanceledException -> ()
      | _ -> Interlocked.Increment(&flushErrors) |> ignore
  }

  do Async.Start(loop, cts.Token)

  member _.TryEnqueue(tenantId : string, payload : 'T) : bool =
    if Volatile.Read(&size) >= maxQueue then
      Interlocked.Increment(&overflowDropped) |> ignore
      false
    else
      queue.Enqueue { tenantId = tenantId; payload = payload }
      Interlocked.Increment(&size) |> ignore
      true

  member _.OverflowDropped = Volatile.Read &overflowDropped
  member _.FlushErrors     = Volatile.Read &flushErrors
  member _.Stop() =
    try cts.Cancel() with _ -> ()
  interface IDisposable with
    member x.Dispose() =
      x.Stop()
      cts.Dispose()

// ---------------------------------------------------------------------------
// Mimir backend
// ---------------------------------------------------------------------------

type MimirOptions =
  { /// Mimir / Cortex / Grafana Cloud Prom-compatible base URL
    /// (without `/api/v1/push`). Example: "http://localhost:9009".
    BaseUrl       : string
    /// Header used to forward the PulseBoard tenant id to the upstream
    /// (Mimir's default is `X-Scope-OrgID`). Pass `None` to suppress.
    OrgIdHeader   : string option
    Bearer        : string option
    FlushMs       : int
    MaxBatch      : int
    QueueCapacity : int }
  static member Default(baseUrl : string) =
    { BaseUrl       = baseUrl.TrimEnd '/'
      OrgIdHeader   = Some "X-Scope-OrgID"
      Bearer        = None
      FlushMs       = defaultFlushMs
      MaxBatch      = defaultMaxBatch
      QueueCapacity = defaultQueueCap }

type MimirMetricBackend(opts : MimirOptions, ?http : HttpClient) =
  let client = defaultArg http sharedClient
  let url = opts.BaseUrl + "/api/v1/push"
  let dropped = ConcurrentDictionary<string, int64 ref>()
  let bumpDrop (tid : string) =
    let cell = dropped.GetOrAdd(tid, fun _ -> ref 0L)
    Interlocked.Increment(&cell.contents) |> ignore

  let flush (batches : (string * (string * PromSample)[])[]) : Async<unit> = async {
    for (tid, pairs) in batches do
      // Group samples by series name within this tenant.
      let bySeries =
        pairs
        |> Array.groupBy fst
        |> Array.map (fun (n, ps) -> n, ps |> Array.map snd)
      let body = encodeRemoteWrite bySeries
      let req =
        buildRequest HttpMethod.Post url opts.OrgIdHeader tid
          opts.Bearer "application/x-protobuf" body
      req.Content.Headers.TryAddWithoutValidation("Content-Encoding", "snappy") |> ignore
      req.Headers.TryAddWithoutValidation("X-Prometheus-Remote-Write-Version", "0.1.0") |> ignore
      try
        let! resp = client.SendAsync(req) |> Async.AwaitTask
        if not resp.IsSuccessStatusCode then
          // Bump per-tenant overflow counter so operators can see fan-out failures
          // (real cardinality drops happen at admission time, not here).
          for _ in pairs do bumpDrop tid
        resp.Dispose()
      with _ ->
        for _ in pairs do bumpDrop tid
  }

  let pump =
    new FlushPump<string * PromSample>(
      opts.QueueCapacity, opts.FlushMs, opts.MaxBatch, flush)

  member _.Stop() = pump.Stop()
  member _.OverflowDropped(tid) =
    match dropped.TryGetValue tid with
    | true, c -> Volatile.Read &c.contents
    | _ -> 0L

  interface IMetricBackend with
    member _.Record(tid, name, p) =
      let sample = { tsMs = p.ts; value = p.value }
      let ok = pump.TryEnqueue(tid, (name, sample))
      if ok then
        WriteOutcome.Accepted
      else
        bumpDrop tid
        // We don't know the cap; surface 0 to mean "queue overflow"
        // (callers only treat the discriminator, not the integer).
        WriteOutcome.DroppedCardinality 0

    member _.Names() = [||]
    member _.Get _   = [||]
    member _.GetSince(_, _) = [||]
    member _.SeriesCount _  = 0
    member _.DroppedCardinality tid =
      match dropped.TryGetValue tid with
      | true, c -> Volatile.Read &c.contents
      | _ -> 0L

  interface IDisposable with
    member x.Dispose() = x.Stop()

// ---------------------------------------------------------------------------
// Loki backend (JSON push)
// ---------------------------------------------------------------------------

type LokiOptions =
  { BaseUrl       : string
    OrgIdHeader   : string option
    Bearer        : string option
    FlushMs       : int
    MaxBatch      : int
    QueueCapacity : int }
  static member Default(baseUrl : string) =
    { BaseUrl       = baseUrl.TrimEnd '/'
      OrgIdHeader   = Some "X-Scope-OrgID"
      Bearer        = None
      FlushMs       = defaultFlushMs
      MaxBatch      = defaultMaxBatch
      QueueCapacity = defaultQueueCap }

/// JSON-encode a `{"streams":[...]}` push body. One stream per log
/// entry (Loki accepts that, and our entries vary by service/level
/// anyway — chunking by `(service,level)` is an obvious follow-up).
let internal encodeLokiJson (entries : LogEntry[]) : byte[] =
  use ms = new MemoryStream()
  use w = new Utf8JsonWriter(ms)
  w.WriteStartObject()
  w.WriteStartArray "streams"
  for e in entries do
    w.WriteStartObject()
    w.WriteStartObject "stream"
    w.WriteString("service", if isNull e.service then "" else e.service)
    w.WriteString("level",   if isNull e.level   then "" else e.level)
    w.WriteEndObject()
    w.WriteStartArray "values"
    w.WriteStartArray()
    // Loki timestamps are nanosecond strings.
    w.WriteStringValue(string (e.ts * 1_000_000L))
    w.WriteStringValue(if isNull e.message then "" else e.message)
    w.WriteEndArray()
    w.WriteEndArray()
    w.WriteEndObject()
  w.WriteEndArray()
  w.WriteEndObject()
  w.Flush()
  ms.ToArray()

type LokiLogBackend(opts : LokiOptions, ?http : HttpClient) =
  let client = defaultArg http sharedClient
  let url = opts.BaseUrl + "/loki/api/v1/push"

  let flush (batches : (string * LogEntry[])[]) : Async<unit> = async {
    for (tid, entries) in batches do
      let body = encodeLokiJson entries
      let req =
        buildRequest HttpMethod.Post url opts.OrgIdHeader tid
          opts.Bearer "application/json" body
      try
        let! resp = client.SendAsync(req) |> Async.AwaitTask
        resp.Dispose()
      with _ -> ()
  }

  let pump =
    new FlushPump<LogEntry>(
      opts.QueueCapacity, opts.FlushMs, opts.MaxBatch, flush)

  member _.Stop() = pump.Stop()

  interface ILogBackend with
    member _.Add(tid, entry) = pump.TryEnqueue(tid, entry) |> ignore
    member _.Tail _ = [||]

  interface IDisposable with
    member x.Dispose() = x.Stop()

// ---------------------------------------------------------------------------
// Tempo backend — OTLP/HTTP passthrough
// ---------------------------------------------------------------------------
//
// Tempo accepts the same wire format the OTLP receiver consumes
// (POST /v1/traces, application/x-protobuf, OTLP
// ExportTraceServiceRequest). Forwarding the raw bytes avoids
// re-encoding and keeps full fidelity.
//
// The base `ITraceBackend` only knows about counts (legacy from when
// PulseBoard tracked traces by N-spans-seen). `IRawTraceBackend`
// extends it with `IngestOtlpProtobuf` so the OTLP receiver can stream
// the raw body through. Programs that want Tempo upload pass the
// instance to `Otlp.traces` via an extra optional argument.

type IRawTraceBackend =
  inherit ITraceBackend
  abstract IngestOtlpProtobuf : tenantId : string * otlpBytes : byte[] -> unit

type TempoOptions =
  { BaseUrl       : string
    OrgIdHeader   : string option
    Bearer        : string option
    FlushMs       : int
    MaxBatch      : int
    QueueCapacity : int }
  static member Default(baseUrl : string) =
    { BaseUrl       = baseUrl.TrimEnd '/'
      OrgIdHeader   = Some "X-Scope-OrgID"
      Bearer        = None
      FlushMs       = defaultFlushMs
      MaxBatch      = defaultMaxBatch
      QueueCapacity = defaultQueueCap }

type TempoTraceBackend(opts : TempoOptions, ?http : HttpClient) =
  let client = defaultArg http sharedClient
  let url = opts.BaseUrl + "/v1/traces"
  let counters = ConcurrentDictionary<string, int64 ref>()

  let flush (batches : (string * byte[][])[]) : Async<unit> = async {
    for (tid, payloads) in batches do
      for body in payloads do
        let req =
          buildRequest HttpMethod.Post url opts.OrgIdHeader tid
            opts.Bearer "application/x-protobuf" body
        try
          let! resp = client.SendAsync(req) |> Async.AwaitTask
          resp.Dispose()
        with _ -> ()
  }

  let pump =
    new FlushPump<byte[]>(
      opts.QueueCapacity, opts.FlushMs, opts.MaxBatch, flush)

  member _.Stop() = pump.Stop()

  interface IRawTraceBackend with
    member _.IngestOtlpProtobuf(tid, bytes) =
      if not (isNull bytes) && bytes.Length > 0 then
        pump.TryEnqueue(tid, bytes) |> ignore

    member _.IncCount(tid, n) =
      if n <> 0 then
        let cell = counters.GetOrAdd(tid, fun _ -> ref 0L)
        Interlocked.Add(&cell.contents, int64 n) |> ignore
    member _.Count tid =
      match counters.TryGetValue tid with
      | true, c -> Volatile.Read &c.contents
      | _ -> 0L

  interface IDisposable with
    member x.Dispose() = x.Stop()

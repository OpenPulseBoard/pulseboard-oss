module PulseBoard.S3SegmentBackend

// S3-compatible object storage backend for metric time-series segments.
// Replaces the local-filesystem SegmentStore (Segments.fs) as the
// "no external TSDB" persistence tier.
//
// Key layout:   <prefix><sanitized-metric>/seg-<ts20>.bin
// File format:  identical to SegmentStore — 16-byte little-endian records
//               [ int64 ts_ms ][ float64 value ].
//
// Compatible S3-compatible stores:
//   * AWS S3                   — leave Endpoint None
//   * SeaweedFS  (recommended) — set Endpoint to SeaweedFS S3 gateway URL
//   * Ceph RGW, Garage, Tigris — same endpoint override pattern
//
// Credentials are resolved by the AWS default credential chain
// (AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY env vars, ~/.aws/credentials,
// IAM role, IRSA). Never pass inline credentials via CLI flags.
//
// Write path:
//   Append() buffers points in memory per metric. When the buffer reaches
//   MaxBytesPerSegment it rotates: the buffer is uploaded to S3 as a new
//   segment object and cleared. The flush timer (wired in Program.fs)
//   calls Flush() every second; Flush rate-limits actual S3 uploads to
//   once per FlushIntervalSec per metric to avoid tiny objects.
//
// Read path:
//   ReadSince() lists relevant segment objects, downloads them, decodes
//   the 16-byte records, and merges in the current in-memory buffer.
//
// Retention:
//   Configure S3 bucket lifecycle rules externally
//   (e.g. aws s3api put-bucket-lifecycle-configuration) or via the
//   object store's own UI. No application-side TTL enforcement needed.

open System
open System.Collections.Concurrent
open System.IO
open System.Threading.Tasks
open Amazon
open Amazon.S3
open Amazon.S3.Model
open PulseBoard.Segments   // sanitize, recordSize
open PulseBoard.TimeSeries // Point

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

type S3Options =
  { /// S3 bucket name.
    Bucket             : string
    /// Key prefix — must end with "/" or be empty.
    /// Default: "metrics/"
    Prefix             : string
    Region             : string option
    /// Endpoint override for SeaweedFS, Ceph, Garage, Tigris, etc.
    /// Leave None for AWS S3.
    Endpoint           : string option
    /// Rotate and upload when the in-memory buffer reaches this size (bytes).
    MaxBytesPerSegment : int64
    /// Minimum seconds between background Flush() uploads per metric.
    FlushIntervalSec   : int }
  static member Default(bucket : string) =
    { Bucket             = bucket
      Prefix             = "metrics/"
      Region             = None
      Endpoint           = None
      MaxBytesPerSegment = 1L <<< 20  // 1 MiB ≈ 65 536 points
      FlushIntervalSec   = 30 }

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

let private mkClient (opts : S3Options) : AmazonS3Client =
  let cfg = AmazonS3Config()
  match opts.Endpoint with
  | Some url ->
    // S3-compatible store (SeaweedFS / Ceph / etc).
    // ServiceURL and RegionEndpoint are mutually exclusive; use
    // AuthenticationRegion for SigV4 signing when endpoint is set.
    cfg.ServiceURL <- url
    cfg.ForcePathStyle <- true
    cfg.AuthenticationRegion <- (defaultArg opts.Region "us-east-1")
  | None ->
    match opts.Region with
    | Some r ->
      try cfg.RegionEndpoint <- RegionEndpoint.GetBySystemName r
      with _ -> ()
    | None -> ()
  new AmazonS3Client(cfg)

let private metricsPrefix (opts : S3Options) =
  if String.IsNullOrEmpty opts.Prefix then ""
  else opts.Prefix.TrimEnd('/') + "/"

let private metricPrefix (opts : S3Options) (metric : string) =
  sprintf "%s%s/" (metricsPrefix opts) (sanitize metric)

let private segKey (opts : S3Options) (metric : string) (startTs : int64) =
  sprintf "%s%s/seg-%020d.bin" (metricsPrefix opts) (sanitize metric) startTs

let private encodePoints (pts : seq<Point>) : byte[] =
  use ms = new MemoryStream()
  for p in pts do
    ms.Write(BitConverter.GetBytes p.ts,    0, 8)
    ms.Write(BitConverter.GetBytes p.value, 0, 8)
  ms.ToArray()

let private decodePoints (bytes : byte[]) (sinceMs : int64) : Point[] =
  let pts = ResizeArray()
  let mutable i = 0
  while i + recordSize <= bytes.Length do
    let ts = BitConverter.ToInt64(bytes, i)
    let v  = BitConverter.ToDouble(bytes, i + 8)
    if ts >= sinceMs then pts.Add { ts = ts; value = v }
    i <- i + recordSize
  pts.ToArray()

// ---------------------------------------------------------------------------
// Per-metric in-memory write buffer
// ---------------------------------------------------------------------------

[<AllowNullLiteral>]
type private MetricBuf() =
  let data    = ResizeArray<Point>()
  let sync    = obj()
  let mutable startTs   = 0L
  let mutable lastFlush = DateTime.MinValue

  /// Add a point; returns Some(bytes, ts) when the buffer should be
  /// rotated (caller uploads and clears), None otherwise.
  member _.TryAppend(p : Point, maxBytes : int64) : (byte[] * int64) option =
    lock sync (fun () ->
      if data.Count = 0 then startTs <- p.ts
      data.Add p
      if int64 data.Count * int64 recordSize >= maxBytes then
        let bytes = encodePoints data
        let ts    = startTs
        data.Clear()
        startTs <- 0L
        Some (bytes, ts)
      else None)

  /// Force-encode the current buffer if it has points AND we haven't
  /// flushed within `minIntervalSec`. Returns Some(bytes, ts) when the
  /// caller should upload; always clears the buffer on Some.
  member _.TryFlush(minIntervalSec : int) : (byte[] * int64) option =
    lock sync (fun () ->
      if data.Count = 0 then None
      else
        let now = DateTime.UtcNow
        if (now - lastFlush).TotalSeconds < float minIntervalSec then None
        else
          let bytes = encodePoints data
          let ts    = startTs
          data.Clear()
          startTs   <- 0L
          lastFlush <- now
          Some (bytes, ts))

  /// Snapshot the current in-memory points (no lock release needed; called
  /// from ReadSince which needs the latest data even if below rotation).
  member _.Snapshot(sinceMs : int64) : Point[] =
    lock sync (fun () ->
      data |> Seq.filter (fun p -> p.ts >= sinceMs) |> Seq.toArray)

// ---------------------------------------------------------------------------
// S3SegmentStore
// ---------------------------------------------------------------------------

type S3SegmentStore(opts : S3Options) =

  let client  = mkClient opts
  let buffers = ConcurrentDictionary<string, MetricBuf>()

  let getBuf (metric : string) =
    buffers.GetOrAdd(metric, fun _ -> MetricBuf())

  let upload (metric : string) (startTs : int64) (bytes : byte[]) =
    let key = segKey opts metric startTs
    try
      use stream = new MemoryStream(bytes)
      let req = PutObjectRequest()
      req.BucketName  <- opts.Bucket
      req.Key         <- key
      req.InputStream <- stream
      req.ContentType <- "application/octet-stream"
      client.PutObjectAsync(req).GetAwaiter().GetResult() |> ignore
    with ex ->
      eprintfn "  [s3-segments] upload failed key=%s: %s" key ex.Message

  /// Hook for MetricStore.SetOnAppend.
  member _.Append (metric : string) (p : Point) =
    let buf = getBuf metric
    match buf.TryAppend(p, opts.MaxBytesPerSegment) with
    | None -> ()
    | Some (bytes, ts) ->
      // Upload on a thread pool thread so the ingest hot path isn't blocked.
      Task.Run(fun () -> upload metric ts bytes) |> ignore

  /// Hook for MetricStore.SetHistory — called when the in-memory ring
  /// doesn't cover the requested window.
  member _.ReadSince (metric : string) (sinceMs : int64) : Point[] =
    // 1. List S3 segment objects for this metric.
    let prefix = metricPrefix opts metric
    let segs =
      try
        let req = ListObjectsV2Request()
        req.BucketName <- opts.Bucket
        req.Prefix     <- prefix
        let resp = client.ListObjectsV2Async(req).GetAwaiter().GetResult()
        resp.S3Objects
        |> Seq.choose (fun o ->
          let name = Path.GetFileNameWithoutExtension o.Key
          if name.StartsWith "seg-" then
            match Int64.TryParse(name.Substring 4) with
            | true, ts -> Some (ts, o.Key)
            | _        -> None
          else None)
        |> Seq.toArray
        |> Array.sortBy fst
      with _ -> [||]

    // 2. Find the starting segment (last one whose ts <= sinceMs).
    let startIdx =
      let lastBefore =
        segs
        |> Array.mapi  (fun i (ts, _) -> i, ts)
        |> Array.filter (fun (_, ts) -> ts <= sinceMs)
        |> Array.tryLast
      match lastBefore with Some (i, _) -> i | None -> 0

    // 3. Download and decode relevant segments.
    let result = ResizeArray<Point>()
    for i in startIdx .. segs.Length - 1 do
      let _, key = segs.[i]
      try
        let req = GetObjectRequest()
        req.BucketName <- opts.Bucket
        req.Key        <- key
        use resp = client.GetObjectAsync(req).GetAwaiter().GetResult()
        use ms   = new MemoryStream()
        resp.ResponseStream.CopyTo(ms)
        let bytes = ms.ToArray()
        for p in decodePoints bytes sinceMs do
          result.Add p
      with ex ->
        eprintfn "  [s3-segments] download failed key=%s: %s" key ex.Message

    // 4. Merge current in-memory buffer.
    let buf = getBuf metric
    for p in buf.Snapshot sinceMs do
      result.Add p

    result.ToArray() |> Array.sortBy (fun p -> p.ts)

  /// Hook for MetricStore.SetExtraNames — lists distinct metric
  /// "directories" under the prefix.
  member _.KnownNames () : string[] =
    let prefix = metricsPrefix opts
    try
      let req = ListObjectsV2Request()
      req.BucketName <- opts.Bucket
      req.Prefix     <- prefix
      req.Delimiter  <- "/"
      let resp = client.ListObjectsV2Async(req).GetAwaiter().GetResult()
      resp.CommonPrefixes
      |> Seq.choose (fun cp ->
        // cp = "metrics/cpu_usage/" — extract "cpu_usage"
        let inner = cp.TrimEnd('/').Substring(prefix.Length)
        if inner.Length > 0 then Some inner else None)
      |> Seq.toArray
    with _ -> [||]

  /// Called by the flush timer — uploads any in-memory buffer that has
  /// not been flushed recently. Rate-limited to opts.FlushIntervalSec
  /// per metric to avoid tiny S3 objects.
  member _.Flush() =
    for kv in buffers do
      match kv.Value.TryFlush(opts.FlushIntervalSec) with
      | None -> ()
      | Some (bytes, ts) ->
        // Fire-and-forget upload so the timer thread isn't blocked.
        let m = kv.Key
        Task.Run(fun () -> upload m ts bytes) |> ignore

  interface IDisposable with
    member x.Dispose() =
      // Best-effort: upload all remaining in-memory data on shutdown.
      for kv in buffers do
        match kv.Value.TryFlush(0) with
        | None -> ()
        | Some (bytes, ts) ->
          try upload kv.Key ts bytes
          with _ -> ()
      try client.Dispose() with _ -> ()

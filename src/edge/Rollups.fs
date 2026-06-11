module PulseBoard.Rollups

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open PulseBoard.TimeSeries

// Per-resolution rollup buckets and the embedded background worker
// that fills them.
//
// Shape:
//   * `Resolution` enumerates the supported bucket widths
//     (1m / 5m / 1h). Each carries its name (`"1m"`, `"5m"`, `"1h"`)
//     and width in milliseconds.
//   * `Bucket` holds count + min + max + sum so the four standard
//     aggregations (avg / min / max / sum / count) can all be served
//     without keeping raw points.
//   * `RollupStore` is a thread-safe map of
//     (metric name, resolution) -> sorted bucket sequence. Each
//     series-resolution pair is capped at `maxBuckets` (oldest
//     evicted) so a long-lived process can't grow unboundedly.
//   * `RollupWorker` walks the embedded `MetricStore` on a timer:
//     snapshot raw points -> regroup -> overwrite the bucket map for
//     the affected series. Each pass is idempotent and re-derives
//     in-progress buckets from raw, so partial-bucket aggregates are
//     correct without bookkeeping.
//
// Multi-tenant caveat (same as retention): the embedded MetricStore
// is process-global so rollups span all tenants. Mimir / Loki / Tempo
// have their own recording-rule / downsampling pipelines and are
// expected to expose rollups via PromQL; the embedded worker is a
// best-effort floor for the OSS / single-binary deployment.

[<RequireQualifiedAccess>]
type Resolution =
  | OneMinute
  | FiveMinutes
  | OneHour
  member this.Name =
    match this with
    | OneMinute   -> "1m"
    | FiveMinutes -> "5m"
    | OneHour     -> "1h"
  member this.Ms : int64 =
    match this with
    | OneMinute   ->     60_000L
    | FiveMinutes ->    300_000L
    | OneHour     ->  3_600_000L

let allResolutions =
  [| Resolution.OneMinute; Resolution.FiveMinutes; Resolution.OneHour |]

let tryParseResolutionMs (ms : int64) : Resolution option =
  allResolutions |> Array.tryFind (fun r -> r.Ms = ms)

[<RequireQualifiedAccess>]
type Agg =
  | Avg
  | Min
  | Max
  | Sum
  | Count
  member this.Name =
    match this with
    | Avg -> "avg" | Min -> "min" | Max -> "max"
    | Sum -> "sum" | Count -> "count"

let tryParseAgg (s : string) : Agg option =
  match s.Trim().ToLowerInvariant() with
  | "avg" | ""    -> Some Agg.Avg
  | "min"         -> Some Agg.Min
  | "max"         -> Some Agg.Max
  | "sum"         -> Some Agg.Sum
  | "count"       -> Some Agg.Count
  | _             -> None

[<NoComparison>]
type Bucket =
  { ts    : int64    // bucket start (unix ms, aligned to resolution)
    count : int
    min   : float
    max   : float
    sum   : float }
  member b.Avg = if b.count > 0 then b.sum / float b.count else 0.0
  member b.Value (agg : Agg) : float =
    match agg with
    | Agg.Avg   -> b.Avg
    | Agg.Min   -> b.min
    | Agg.Max   -> b.max
    | Agg.Sum   -> b.sum
    | Agg.Count -> float b.count

let bucketStart (tsMs : int64) (resMs : int64) : int64 =
  tsMs - (tsMs % resMs)

/// Aggregate a sequence of raw points into bucket records. Output is
/// sorted by bucket start.
let aggregate (resMs : int64) (points : Point seq) : Bucket[] =
  let map = SortedDictionary<int64, Bucket>()
  for p in points do
    let key = bucketStart p.ts resMs
    match map.TryGetValue key with
    | true, b ->
      map.[key] <-
        { b with
            count = b.count + 1
            min   = min b.min p.value
            max   = max b.max p.value
            sum   = b.sum + p.value }
    | _ ->
      map.[key] <-
        { ts = key; count = 1; min = p.value; max = p.value; sum = p.value }
  let arr = Array.zeroCreate map.Count
  let mutable i = 0
  for kv in map do
    arr.[i] <- kv.Value
    i <- i + 1
  arr

/// Per-(name, resolution) bucket cache. Each replace is O(buckets)
/// behind a per-series lock; reads snapshot under the same lock.
type RollupStore(maxBucketsPerSeries : int) =

  // outer key: metric name; inner key: resolution ms.
  let series =
    ConcurrentDictionary<string, ConcurrentDictionary<int64, obj * Bucket[]>>()

  let getInner (name : string) =
    series.GetOrAdd(name, fun _ -> ConcurrentDictionary<int64, obj * Bucket[]>())

  member _.MaxBucketsPerSeries = maxBucketsPerSeries

  /// Replace the bucket array for (name, resMs). Trims to the most
  /// recent `maxBucketsPerSeries` entries if oversize.
  member _.Replace(name : string, resMs : int64, buckets : Bucket[]) =
    let trimmed =
      if buckets.Length <= maxBucketsPerSeries then buckets
      else buckets.[buckets.Length - maxBucketsPerSeries ..]
    let inner = getInner name
    let lockObj : obj = box (name + ":" + string resMs)
    inner.[resMs] <- (lockObj, trimmed)

  member _.Get(name : string, resMs : int64) : Bucket[] =
    match series.TryGetValue name with
    | false, _ -> [||]
    | true, inner ->
      match inner.TryGetValue resMs with
      | true, (_, b) -> b
      | _            -> [||]

  member x.GetSince(name : string, resMs : int64, sinceMs : int64) : Bucket[] =
    x.Get(name, resMs)
    |> Array.filter (fun b -> b.ts >= sinceMs)

  /// Materialise (ts, value) pairs for the chosen aggregation.
  member x.GetSinceAgg(name : string, resMs : int64,
                       sinceMs : int64, agg : Agg) : Point[] =
    x.GetSince(name, resMs, sinceMs)
    |> Array.map (fun b -> { ts = b.ts; value = b.Value agg })

  /// Drop all buckets older than `cutoffMs`. Useful so rollups don't
  /// retain points the retention compactor already evicted from raw.
  member _.PruneOlderThan(cutoffMs : int64) : int =
    let mutable dropped = 0
    for kv in series do
      let inner = kv.Value
      for kv2 in inner do
        let (lockObj, arr) = kv2.Value
        let kept = arr |> Array.filter (fun b -> b.ts >= cutoffMs)
        if kept.Length <> arr.Length then
          dropped <- dropped + (arr.Length - kept.Length)
          inner.[kv2.Key] <- (lockObj, kept)
    dropped

/// Periodically refreshes the rollup store from the embedded
/// MetricStore. Each pass snapshots every known metric and re-derives
/// the buckets for every configured resolution. Cheap enough at
/// the embedded scale (a 4k-capacity ring per metric x 3 resolutions =
/// ~12k aggregations per series per pass).
type RollupWorker(metricStore : MetricStore,
                  rollupStore : RollupStore,
                  resolutions : Resolution[],
                  intervalMs  : int) =

  let cts = new CancellationTokenSource()
  let mutable lastRunAtMs = 0L
  let mutable lastSeriesProcessed = 0
  let mutable lastBucketsWritten  = 0

  /// Run one rollup pass synchronously. Returns
  /// `(seriesProcessed, bucketsWritten)`.
  member _.RunOnce() : int * int =
    let names = metricStore.Names()
    let mutable buckets = 0
    for name in names do
      let raw = metricStore.Get name
      if raw.Length > 0 then
        for r in resolutions do
          let agg = aggregate r.Ms raw
          rollupStore.Replace(name, r.Ms, agg)
          buckets <- buckets + agg.Length
    lastRunAtMs         <- nowMs ()
    lastSeriesProcessed <- names.Length
    lastBucketsWritten  <- buckets
    names.Length, buckets

  member x.Start() =
    let loop = async {
      while not cts.IsCancellationRequested do
        try
          x.RunOnce() |> ignore
        with ex ->
          eprintfn "[rollups] worker pass failed: %s" ex.Message
        do! Async.Sleep intervalMs
    }
    Async.Start(loop, cts.Token)

  member _.LastRunAtMs         = lastRunAtMs
  member _.LastSeriesProcessed = lastSeriesProcessed
  member _.LastBucketsWritten  = lastBucketsWritten
  member _.IntervalMs          = intervalMs
  member _.Resolutions         = resolutions

  member _.Stop() =
    try cts.Cancel() with _ -> ()

  interface IDisposable with
    member x.Dispose() =
      x.Stop()
      cts.Dispose()

/// Heuristic: pick the coarsest resolution that yields a reasonable
/// number of buckets for the requested window. Returns `None` for
/// "use raw points" (window short enough that raw resolution is fine).
///
/// Thresholds (window length):
///   <  1h           -> raw
///   1h .. 12h       -> 1m
///   12h .. 7d       -> 5m
///   >= 7d           -> 1h
let autoResolution (windowMs : int64) : Resolution option =
  if   windowMs < 3_600_000L         then None                          // <1h: raw
  elif windowMs < 12L * 3_600_000L   then Some Resolution.OneMinute     // <12h
  elif windowMs < 7L * 86_400_000L   then Some Resolution.FiveMinutes   // <7d
  else                                    Some Resolution.OneHour

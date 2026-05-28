module PulseBoard.Bench.Helpers

open System
open System.IO
open System.Text
open PulseBoard.TimeSeries
open PulseBoard.Tenancy
open PulseBoard.Audit
open PulseBoard.Hub
open PulseBoard.Storage
open PulseBoard.Gateway
open PulseBoard.NotifyQueue
open Snappier
open Google.Protobuf

// ---------------------------------------------------------------------------
// Shared test-data builders and store factories used across all benchmarks.
// ---------------------------------------------------------------------------

let nowMs () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

// ---------------------------------------------------------------------------
// Store factory (mirrors TestEdge.makeStores but without Suave overhead)
// ---------------------------------------------------------------------------

let makeMetricStore () = MetricStore(capacityPerMetric = 4096)
let makeLogStore    () = LogStore(capacity = 4096)

let makeStorageClient () =
    let ms  = makeMetricStore ()
    let ls  = makeLogStore ()
    let hub = Broadcaster()
    let mb  = EmbeddedMetricBackend(ms, None) :> IMetricBackend
    let lb  = EmbeddedLogBackend(ls)          :> ILogBackend
    let tb  = EmbeddedTraceBackend()          :> ITraceBackend
    ms, ls, InProcessStorageClient(mb, lb, tb, hub) :> IStorageClient

// ---------------------------------------------------------------------------
// Pre-seeded stores with N distinct metric series, each with K points.
// ---------------------------------------------------------------------------

let seedMetrics (store : MetricStore) (seriesCount : int) (pointsPerSeries : int) =
    let baseTs = nowMs () - int64 pointsPerSeries * 1000L
    for s in 0 .. seriesCount - 1 do
        let name = sprintf "bench_metric_%d{host=\"h%d\"}" s s
        for k in 0 .. pointsPerSeries - 1 do
            store.Record(name, { ts = baseTs + int64 k * 1000L; value = float k })

let seedLogs (store : LogStore) (entryCount : int) =
    let baseTs = nowMs () - int64 entryCount * 1000L
    for i in 0 .. entryCount - 1 do
        store.Add
            { ts      = baseTs + int64 i * 1000L
              service = sprintf "svc-%d" (i % 10)
              level   = if i % 5 = 0 then "error" else "info"
              message = sprintf "log entry %d — the quick brown fox" i }

// ---------------------------------------------------------------------------
// Minimal hand-encoded protobuf helpers (same as ProtocolIngestTests)
// ---------------------------------------------------------------------------

module Proto =

    let private varintOf (n : uint64) : byte[] =
        let buf = Array.zeroCreate 10
        let mutable v = n
        let mutable i = 0
        while v > 127UL do
            buf.[i] <- byte (v ||| 0x80UL)
            v <- v >>> 7
            i <- i + 1
        buf.[i] <- byte v
        Array.sub buf 0 (i + 1)

    let private uint64LE (n : uint64) : byte[] =
        [| byte n; byte (n >>> 8); byte (n >>> 16); byte (n >>> 24)
           byte (n >>> 32); byte (n >>> 40); byte (n >>> 48); byte (n >>> 56) |]

    let tag (field : int) (wireType : int) : byte[] =
        varintOf (uint64 ((field <<< 3) ||| wireType))

    let lengthDelimited (field : int) (data : byte[]) : byte[] =
        Array.concat [ tag field 2; varintOf (uint64 data.Length); data ]

    let stringField (field : int) (s : string) : byte[] =
        lengthDelimited field (Encoding.UTF8.GetBytes s)

    let doubleField (field : int) (v : float) : byte[] =
        Array.concat [ tag field 1; uint64LE (BitConverter.ToUInt64(BitConverter.GetBytes v, 0)) ]

    let int64Field (field : int) (v : int64) : byte[] =
        Array.concat [ tag field 0; varintOf (uint64 v) ]

    let fixed64Field (field : int) (v : uint64) : byte[] =
        Array.concat [ tag field 1; uint64LE v ]

    /// Encode a single-sample Prometheus WriteRequest protobuf.
    let buildWriteRequest (metricName : string) (value : float) (tsMs : int64) : byte[] =
        let nameLabel  = Array.concat [ stringField 1 "__name__"; stringField 2 metricName ]
        let label      = lengthDelimited 1 nameLabel
        let sample     = Array.concat [ doubleField 1 value; int64Field 2 tsMs ]
        let samplePb   = lengthDelimited 2 sample
        let timeSeries = Array.concat [ label; samplePb ]
        lengthDelimited 1 timeSeries

    /// Encode an OTLP ExportMetricsServiceRequest with one gauge data point.
    let buildOtlpMetrics (metricName : string) (value : float) (tsNano : uint64) : byte[] =
        let ndp          = Array.concat [ fixed64Field 3 tsNano; doubleField 4 value ]
        let gauge        = lengthDelimited 1 ndp
        let metric       = Array.concat [ stringField 1 metricName; lengthDelimited 5 gauge ]
        let scopeMetrics = lengthDelimited 2 metric
        let resourceMetrics = lengthDelimited 2 scopeMetrics
        lengthDelimited 1 resourceMetrics

    /// Snappy-compress a WriteRequest ready to POST to /api/v1/write.
    let buildCompressedWriteRequest (metricName : string) (value : float) (tsMs : int64) : byte[] =
        let proto = buildWriteRequest metricName value tsMs
        Snappy.CompressToArray(ReadOnlySpan proto)

// ---------------------------------------------------------------------------
// NotifyQueue helpers
// ---------------------------------------------------------------------------

let makeTempQueue () : INotifyQueue * string =
    let d = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory d |> ignore
    FileNotifyQueue(d) :> INotifyQueue, d

let makeMessage (i : int) : OutboundMessage =
    let now = nowMs ()
    { id           = Guid.NewGuid().ToString("N")
      tenantId     = TenantId (sprintf "tenant-%d" (i % 5))
      receiverId   = sprintf "recv-%d" (i % 3)
      receiverType = "webhook"
      url          = "http://localhost:9999/sink"
      secret       = None
      body         = sprintf """{"alerts":[{"labels":{"alertname":"bench_%d"}}]}""" i
      headers      = Map.empty
      extra        = Map.empty
      attempt      = 0
      maxAttempts  = 5
      enqueuedAt   = now
      nextRunAt    = now
      lastError    = None }

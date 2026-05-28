module PulseBoard.Bench.IngestBenchmarks

open System
open BenchmarkDotNet.Attributes
open PulseBoard.TimeSeries
open PulseBoard.Bench.Helpers

// ---------------------------------------------------------------------------
// Ingest throughput benchmarks.
//
// Measures the hot path for each ingest receiver in isolation — no HTTP,
// no network, just the storage write path. Use these to catch regressions
// in the ring-buffer and embedded backend.
//
// Benchmark suite:
//   MetricRecord   — single MetricStore.Record call (baseline)
//   LogAppend      — single LogStore.Append call
//   MetricBurst100 — 100 Record calls (simulates a /ingest/metrics batch)
//   LogBurst100    — 100 Append calls
//   PromWriteEncode  — Snappy-encode a WriteRequest (CPU cost before storage)
//   OtlpEncode       — Build an OTLP ExportMetricsServiceRequest
// ---------------------------------------------------------------------------

[<MemoryDiagnoser>]
[<SimpleJob(BenchmarkDotNet.Jobs.RuntimeMoniker.Net80)>]
type IngestBenchmarks () =

    let mutable metricStore : MetricStore = Unchecked.defaultof<_>
    let mutable logStore    : LogStore    = Unchecked.defaultof<_>

    // Pre-build a fixed compressed payload so encode cost is excluded from
    // the StorageWrite benchmarks and separately measured in PromWriteEncode.
    let mutable compressedWriteReq : byte[] = [||]
    let mutable otlpPayload        : byte[] = [||]

    [<GlobalSetup>]
    member _.Setup () =
        metricStore <- makeMetricStore ()
        logStore    <- makeLogStore ()
        compressedWriteReq <-
            Proto.buildCompressedWriteRequest "bench_metric" 42.0
                (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        otlpPayload <-
            Proto.buildOtlpMetrics "bench_otlp" 1.0
                (uint64 (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) * 1_000_000UL)

    // -- Raw store operations -----------------------------------------------

    [<Benchmark(Baseline = true)>]
    member _.MetricRecord () =
        metricStore.Record("bench_cpu", { ts = nowMs (); value = 0.75 })

    [<Benchmark>]
    member _.LogAppend () =
        logStore.Add
            { ts = nowMs (); service = "svc"; level = "info"
              message = "benchmark log entry" }

    // -- Burst operations (simulate a realistic ingest batch) ---------------

    [<Benchmark>]
    [<Arguments(100)>]
    [<Arguments(1000)>]
    member _.MetricBurst (n : int) =
        let ts = nowMs ()
        for i in 0 .. n - 1 do
            metricStore.Record(
                sprintf "bench_cpu{host=\"h%d\"}" (i % 10),
                { ts = ts + int64 i; value = float i * 0.01 })

    [<Benchmark>]
    [<Arguments(100)>]
    [<Arguments(1000)>]
    member _.LogBurst (n : int) =
        let ts = nowMs ()
        for i in 0 .. n - 1 do
            logStore.Add
                { ts = ts + int64 i; service = "svc"
                  level = "info"; message = sprintf "line %d" i }

    // -- Encode-only (no storage write) ------------------------------------

    [<Benchmark>]
    member _.PromWriteEncode () =
        Proto.buildCompressedWriteRequest "bench_rw" 1.0
            (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        |> ignore

    [<Benchmark>]
    member _.OtlpMetricEncode () =
        Proto.buildOtlpMetrics "bench_otlp" 1.0
            (uint64 (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) * 1_000_000UL)
        |> ignore

    // -- Read path (snapshot baseline) -------------------------------------

    [<Benchmark>]
    member _.MetricGet () =
        metricStore.Get("bench_cpu") |> ignore

    [<Benchmark>]
    member _.LogSnapshot () =
        logStore.Snapshot() |> ignore

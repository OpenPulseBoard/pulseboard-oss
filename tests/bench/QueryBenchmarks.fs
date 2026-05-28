module PulseBoard.Bench.QueryBenchmarks

open System
open BenchmarkDotNet.Attributes
open PulseBoard.TimeSeries
open PulseBoard.Rollups
open PulseBoard.Bench.Helpers

// ---------------------------------------------------------------------------
// Query p99 benchmarks: raw vs. rolled-up series.
//
// We pre-seed realistic data volumes and measure the cost of:
//   - Snapshot + filter (the fast path for small workspaces)
//   - RollupWorker.RunOnce() (background aggregation pass)
//   - RollupStore.GetSinceAgg() (query against pre-built rollups)
//
// Parameterised on series count so we can see how latency scales.
// ---------------------------------------------------------------------------

[<MemoryDiagnoser>]
[<SimpleJob(BenchmarkDotNet.Jobs.RuntimeMoniker.Net80)>]
type QueryBenchmarks () =

    // F# rule: all let bindings must precede member declarations.
    let pointsPerSeries = 1000   // 1k points each ≈ 1 000 samples / series

    let mutable metricStore  : MetricStore  = Unchecked.defaultof<_>
    let mutable rollupStore  : RollupStore  = Unchecked.defaultof<_>
    let mutable rollupWorker : RollupWorker = Unchecked.defaultof<_>
    let mutable logStore     : PulseBoard.TimeSeries.LogStore = Unchecked.defaultof<_>

    let resolutions : Resolution[] =
        [| Resolution.OneMinute; Resolution.FiveMinutes; Resolution.OneHour |]

    [<Params(10, 100, 500)>]
    member val SeriesCount = 10 with get, set

    [<GlobalSetup>]
    member bench.Setup () =
        metricStore <- makeMetricStore ()
        seedMetrics metricStore bench.SeriesCount pointsPerSeries
        logStore     <- makeLogStore ()
        seedLogs logStore 2000
        rollupStore  <- RollupStore(maxBucketsPerSeries = 1440)
        rollupWorker <- RollupWorker(metricStore, rollupStore, resolutions, intervalMs = 60_000)
        // Pre-compute rollups so GetSinceAgg benchmarks measure query, not setup.
        rollupWorker.RunOnce() |> ignore

    // -- Raw read -----------------------------------------------------------

    [<Benchmark(Baseline = true)>]
    member bench.RawGetAllSeries () =
        // Simulates the worst-case query: read every series, no filtering.
        for i in 0 .. bench.SeriesCount - 1 do
            metricStore.Get(sprintf "bench_metric_%d{host=\"h%d\"}" i i) |> ignore

    [<Benchmark>]
    member bench.RawGetSince1h () =
        let since = nowMs () - 3_600_000L
        for i in 0 .. bench.SeriesCount - 1 do
            metricStore.GetSince(sprintf "bench_metric_%d{host=\"h%d\"}" i i, since) |> ignore

    [<Benchmark>]
    member bench.MetricNames () =
        metricStore.Names() |> ignore

    // -- Rollup query -------------------------------------------------------

    [<Benchmark>]
    member bench.RollupGetSince1h () =
        let since = nowMs () - 3_600_000L
        for i in 0 .. bench.SeriesCount - 1 do
            rollupStore.GetSinceAgg(
                sprintf "bench_metric_%d{host=\"h%d\"}" i i,
                3_600_000L, since, Agg.Avg) |> ignore

    [<Benchmark>]
    member bench.RollupGetSince1d () =
        let since = nowMs () - 86_400_000L
        for i in 0 .. bench.SeriesCount - 1 do
            rollupStore.GetSinceAgg(
                sprintf "bench_metric_%d{host=\"h%d\"}" i i,
                86_400_000L, since, Agg.Max) |> ignore

    // -- Rollup computation (background worker) ----------------------------

    [<Benchmark>]
    member _.RollupWorkerRunOnce () =
        rollupWorker.RunOnce() |> ignore

    // -- Log snapshot -------------------------------------------------------

    [<Benchmark>]
    member _.LogSnapshot () =
        logStore.Snapshot() |> ignore

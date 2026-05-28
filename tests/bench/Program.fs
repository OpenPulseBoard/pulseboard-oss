module PulseBoard.Bench.Program

open BenchmarkDotNet.Running

// ---------------------------------------------------------------------------
// Entry point — run all benchmarks in this assembly.
//
// Usage (from repo root):
//   dotnet run --project tests/bench/PulseBoard.Bench.fsproj -c Release
//
// Filter to a specific class:
//   dotnet run --project tests/bench/PulseBoard.Bench.fsproj -c Release \
//     -- --filter "*Ingest*"
//
// Export to JSON for CI diff:
//   dotnet run ... -- --exporters Json --artifacts ./bench-results/
//
// Available filters:
//   *Ingest*       — IngestBenchmarks (storage write throughput)
//   *Query*        — QueryBenchmarks  (raw vs rollup read p99)
//   *AlertEval*    — AlertEvalBenchmarks (rule evaluation per group)
//   *NotifyQueue*  — NotifyQueueBenchmarks (enqueue/dispatch/ack)
// ---------------------------------------------------------------------------

[<EntryPoint>]
let main argv =
    let switcher =
        BenchmarkSwitcher.FromTypes(
            [| typeof<IngestBenchmarks.IngestBenchmarks>
               typeof<QueryBenchmarks.QueryBenchmarks>
               typeof<AlertEvalBenchmarks.AlertEvalBenchmarks>
               typeof<NotifyQueueBenchmarks.NotifyQueueBenchmarks> |])
    switcher.Run(argv) |> ignore
    0

module PulseBoard.Bench.NotifyQueueBenchmarks

open System
open System.IO
open BenchmarkDotNet.Attributes
open PulseBoard.TimeSeries
open PulseBoard.NotifyQueue
open PulseBoard.Bench.Helpers

// ---------------------------------------------------------------------------
// NotifyQueue enqueue / dispatch benchmarks (FileNotifyQueue).
//
// The queue is file-backed (journal + DLQ). These benchmarks measure the
// steady-state cost of enqueue, lease/ack round-trips, and the compact
// threshold. Each benchmark class owns its own temp directory, torn down
// in GlobalCleanup.
//
// Benchmark suite:
//   Enqueue       — single message write + journal append
//   EnqueueBatch  — N sequential enqueues (N = 10, 100, 1000)
//   LeaseAndAck   — lease 1 message + ack it (full happy path)
//   LeaseAndFail  — lease 1 message + fail it (retry backoff path)
// ---------------------------------------------------------------------------

[<MemoryDiagnoser>]
[<SimpleJob(BenchmarkDotNet.Jobs.RuntimeMoniker.Net80)>]
type NotifyQueueBenchmarks () =

    let mutable queueDir : string     = ""
    let mutable queue    : INotifyQueue = Unchecked.defaultof<_>

    [<GlobalSetup>]
    member _.Setup () =
        queueDir <- Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory queueDir |> ignore
        queue    <- FileNotifyQueue(queueDir) :> INotifyQueue
        // Pre-fill so Lease/Ack benchmarks always have messages available.
        for i in 0 .. 999 do
            queue.Enqueue(makeMessage i)

    [<GlobalCleanup>]
    member _.Cleanup () =
        try Directory.Delete(queueDir, true) with _ -> ()

    // -- Enqueue (hot path) ------------------------------------------------

    [<Benchmark(Baseline = true)>]
    member _.Enqueue () =
        queue.Enqueue(makeMessage 0)

    [<Benchmark>]
    [<Arguments(10)>]
    [<Arguments(100)>]
    [<Arguments(1000)>]
    member _.EnqueueBatch (n : int) =
        for i in 0 .. n - 1 do
            queue.Enqueue(makeMessage i)

    // -- Lease / Ack round-trip --------------------------------------------

    [<Benchmark>]
    member _.LeaseAndAck () =
        let msgs = queue.Lease(1, nowMs ())
        for m in msgs do
            queue.Ack(m.id)

    [<Benchmark>]
    member _.LeaseAndFail () =
        let msgs = queue.Lease(1, nowMs ())
        for m in msgs do
            queue.Fail(m.id, "simulated failure", nowMs () + 30_000L)

    // -- Batch lease -------------------------------------------------------

    [<Benchmark>]
    [<Arguments(10)>]
    [<Arguments(100)>]
    member _.LeaseBatch (n : int) =
        let msgs = queue.Lease(n, nowMs ())
        for m in msgs do
            queue.Ack(m.id)

    // -- Pending query -----------------------------------------------------

    [<Benchmark>]
    member _.PendingAll () =
        queue.Pending(None) |> ignore

    [<Benchmark>]
    member _.PendingTenant () =
        queue.Pending(Some (PulseBoard.Tenancy.TenantId "tenant-0")) |> ignore

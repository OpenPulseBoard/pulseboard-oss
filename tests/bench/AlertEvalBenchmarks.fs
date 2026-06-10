module PulseBoard.Bench.AlertEvalBenchmarks

open System
open System.IO
open BenchmarkDotNet.Attributes
open PulseBoard.Tenancy
open PulseBoard.TimeSeries
open PulseBoard.Audit
open PulseBoard.Rules
open PulseBoard.Bench.Helpers

// ---------------------------------------------------------------------------
// Alert evaluation benchmarks.
//
// Measures the cost of RuleStore.List + the Evaluator's per-tenant scan
// under varying rule group counts. The evaluator's tick() loop is internal,
// so we time SetTenantsProvider-gated Start/Active cycles that reflect the
// real wall-clock overhead per evaluation pass.
//
// Two scenarios:
//   EvalPassNGroups — N rule groups, each with 1 PromQL rule that fires.
//   ActiveAlert     — query active alert state for a tenant after seeding.
// ---------------------------------------------------------------------------

[<MemoryDiagnoser>]
[<SimpleJob(BenchmarkDotNet.Jobs.RuntimeMoniker.Net80)>]
type AlertEvalBenchmarks () =

    // F# rule: all let bindings must precede member declarations.
    let mutable metricStore  : MetricStore   = Unchecked.defaultof<_>
    let mutable logStore     : LogStore      = Unchecked.defaultof<_>
    let mutable ruleStoreDir : string        = ""
    let mutable ruleStore    : IRuleStore    = Unchecked.defaultof<_>
    let mutable auditLog     : IAuditLog     = Unchecked.defaultof<_>
    let tid = TenantId "bench-tenant"

    let makeSink () =
        { new IAlertSink with
            member _.OnAlert _ = () }

    let makeGroup (i : int) (metricName : string) : RuleGroup =
        let rule : Rule =
            { id          = sprintf "rule-%d" i
              name        = sprintf "BenchAlert%d" i
              lang        = PromQL
              expr        = metricName   // bare metric name — vector selector
              cmp         = Gt
              threshold   = 0.5
              forMs       = 0L           // immediate fire
              severity    = Severity.Warning
              labels      = Map.ofList [ "alertname", sprintf "bench_%d" i ]
              annotations = Map.empty
              runbook     = None }
        { id         = sprintf "group-%d" i
          name       = sprintf "BenchGroup%d" i
          intervalMs = 60_000L
          rules      = [| rule |]
          createdAt  = DateTimeOffset.UtcNow
          updatedAt  = DateTimeOffset.UtcNow }

    [<Params(1, 10, 50)>]
    member val GroupCount = 1 with get, set

    [<GlobalSetup>]
    member bench.Setup () =
        metricStore <- makeMetricStore ()
        logStore    <- makeLogStore ()
        auditLog    <- InMemoryAuditLog(512) :> IAuditLog
        ruleStoreDir <- Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
        ruleStore    <- FileRuleStore(ruleStoreDir) :> IRuleStore

        for i in 0 .. bench.GroupCount - 1 do
            let name = sprintf "bench_alert_metric_%d" i
            let ts = nowMs ()
            for k in 0 .. 99 do
                metricStore.Record(name, { ts = ts - int64 k * 1000L; value = float (k + 60) * 0.01 })
            ruleStore.Upsert(tid, makeGroup i name)

    [<GlobalCleanup>]
    member _.Cleanup () =
        try Directory.Delete(ruleStoreDir, true) with _ -> ()

    // -- Rule store list (no evaluation) ------------------------------------

    [<Benchmark(Baseline = true)>]
    member _.RuleStoreList () =
        ruleStore.List(tid) |> ignore

    // -- Active alert query -------------------------------------------------

    [<Benchmark>]
    member _.EvaluatorActive () =
        let selfStore = makeMetricStore ()
        let ev = Evaluator(metricStore, logStore, ruleStore, makeSink (), selfStore)
        let result = ev.Active(tid)
        ev.Stop()
        result |> ignore

    // -- Full evaluator start / stop cycle ----------------------------------

    [<Benchmark>]
    member _.EvaluatorStartStop () =
        let selfStore = makeMetricStore ()
        let ev = Evaluator(metricStore, logStore, ruleStore, makeSink (), selfStore)
        ev.SetTenantsProvider(fun () -> [| tid |])
        ev.Start()
        System.Threading.Thread.Sleep 5
        ev.Stop()

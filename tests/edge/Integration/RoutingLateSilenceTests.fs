module PulseBoard.Tests.Integration.RoutingLateSilenceTests

open System
open System.IO
open System.Threading
open Xunit
open FsUnit.Xunit
open PulseBoard.Tenancy
open PulseBoard.TimeSeries
open PulseBoard.Rules
open PulseBoard.Routing
open PulseBoard.NotifyQueue

// Regression: a silence created AFTER an alert has already been routed
// must suppress the periodic flush-timer follow-ups. Previously the
// pipeline only consulted silences inside `OnAlert`, which is invoked
// only on Pending→Firing transitions. Once the fingerprint was sitting
// in a group bucket, flushDue happily re-sent it every groupIntervalMs.

[<Trait("Category", "Integration")>]
type RoutingLateSilenceTests () =

    let tempDir () =
        let d = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString "N")
        Directory.CreateDirectory d |> ignore
        d

    let baseConfig (receiverId : string) : Config =
        { route =
            { id              = "root"
              matchers        = [||]
              receiverId      = Some receiverId
              policyId        = None
              groupBy         = [| "alertname" |]
              groupWaitMs     = 0L
              // Small followup interval so the test doesn't have to wait 5 min.
              groupIntervalMs = 500L
              repeatIntervalMs = 0L
              continue_       = false
              muteTimeIds     = [||]
              children        = [||] }
          receivers =
            [| { id     = receiverId
                 name   = "test-webhook"
                 type_  = "webhook"
                 url    = Some "http://localhost:9999/sink"
                 secret = None
                 extra  = Map.empty } |]
          silences    = [||]
          inhibitions = [||]
          muteTimes   = [||] }

    let makeAlert (tid : TenantId) : AlertInstance =
        let labels =
            Map.ofList
                [ "alertname", "Low disk space"
                  "instance",  "mercury"
                  "severity",  "critical" ]
        let fp = fingerprint "rule-disk" labels
        let nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        { fingerprint = fp
          tenantId    = tid
          ruleId      = "rule-disk"
          ruleName    = "Low disk space"
          groupId     = "host-alerts"
          severity    = Severity.Critical
          labels      = labels
          annotations = Map.empty
          value       = 7.14
          state       = AlertState.Firing
          activeAt    = nowMs
          firedAt     = Some nowMs
          resolvedAt  = None
          lastEvalAt  = nowMs
          runbook     = None }

    [<Fact>]
    member _.``silence added after alert routed suppresses subsequent flush followups`` () =
        let queueDir  = tempDir ()
        let configDir = tempDir ()
        try
            let tid         = TenantId "tenant-late-silence"
            let receiverId  = "recv-late"
            let queue       = FileNotifyQueue(queueDir) :> INotifyQueue
            let selfStore   = MetricStore(capacityPerMetric = 256)
            let configStore = FileConfigStore(configDir) :> IConfigStore
            configStore.Set(tid, baseConfig receiverId)

            let pipeline = new Pipeline(configStore, queue, selfStore)

            // 1. Alert fires → routed → first flush dispatches one message.
            let a = makeAlert tid
            pipeline.OnAlert a
            Thread.Sleep 1500
            let initialCount = queue.Pending(None).Length
            initialCount |> should equal 1

            // 2. Operator silences "Low disk space" on "mercury" — matchers
            //    chosen to mirror the SPA "quick silence" button.
            let now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            let sil =
                { id        = "s-late"
                  matchers  =
                    [| { name = "alertname"; op = MEq
                         value = "Low disk space"; re = None }
                       { name = "instance"; op = MEq
                         value = "mercury"; re = None } |]
                  startsAt  = now - 1000L
                  endsAt    = now + 3_600_000L
                  createdBy = "operator"
                  comment   = "silenced from Alerts view"
                  createdAt = now }
            configStore.UpsertSilence(tid, sil)

            // 3. Wait several flush + groupInterval cycles. Without the fix
            //    the queue would gain more pending messages every 500 ms.
            Thread.Sleep 2500
            let finalCount = queue.Pending(None).Length

            pipeline.Stop()

            finalCount |> should equal initialCount
        finally
            try Directory.Delete(queueDir,  true) with _ -> ()
            try Directory.Delete(configDir, true) with _ -> ()

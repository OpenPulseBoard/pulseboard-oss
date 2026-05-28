module PulseBoard.Tests.Unit.RetentionTests

open System
open Xunit
open FsUnit.Xunit
open PulseBoard.Retention
open PulseBoard.TimeSeries
open PulseBoard.Tenancy

// -- helpers ----------------------------------------------------------------

let private tid1 = TenantId "t1"
let private tid2 = TenantId "t2"

let private policy metricsMs logsMs tracesMs : RetentionPolicy =
    { metricsMs = metricsMs; logsMs = logsMs; tracesMs = tracesMs }

let private defaults = policy (Some 86_400_000L) (Some 604_800_000L) None   // 1d / 7d / forever
let private noDefaults = RetentionPolicy.Empty

// -- RetentionStore.Effective -----------------------------------------------

[<Fact>]
let ``Effective returns defaults when no override is set`` () =
    let store = RetentionStore(defaults, InMemoryRetentionRepo())
    let eff   = store.Effective tid1
    eff.metricsMs         |> should equal (Some 86_400_000L)
    eff.logsMs            |> should equal (Some 604_800_000L)
    eff.tracesMs          |> should equal None
    eff.metricsOverridden |> should be False
    eff.logsOverridden    |> should be False

[<Fact>]
let ``Effective returns override when set — override wins over default`` () =
    let store = RetentionStore(defaults, InMemoryRetentionRepo())
    store.SetOverride(tid1, policy (Some 3_600_000L) None None)
    let eff = store.Effective tid1
    eff.metricsMs         |> should equal (Some 3_600_000L)  // override
    eff.logsMs            |> should equal (Some 604_800_000L) // default
    eff.metricsOverridden |> should be True
    eff.logsOverridden    |> should be False

[<Fact>]
let ``Effective falls back to default when override has no value for that field`` () =
    let store = RetentionStore(defaults, InMemoryRetentionRepo())
    // Override only traces; metrics and logs should still come from defaults
    store.SetOverride(tid1, policy None None (Some 3_600_000L))
    let eff = store.Effective tid1
    eff.metricsMs      |> should equal (Some 86_400_000L)
    eff.logsMs         |> should equal (Some 604_800_000L)
    eff.tracesMs       |> should equal (Some 3_600_000L)
    eff.tracesOverridden |> should be True

[<Fact>]
let ``SetOverride with all-None policy removes the override entry`` () =
    let store = RetentionStore(defaults, InMemoryRetentionRepo())
    store.SetOverride(tid1, policy (Some 3_600_000L) None None)
    store.SetOverride(tid1, RetentionPolicy.Empty)         // clear
    let eff = store.Effective tid1
    eff.metricsMs         |> should equal (Some 86_400_000L)
    eff.metricsOverridden |> should be False

[<Fact>]
let ``ClearOverride restores full defaults`` () =
    let store = RetentionStore(defaults, InMemoryRetentionRepo())
    store.SetOverride(tid1, policy (Some 100L) None None)
    store.ClearOverride tid1
    store.Effective(tid1).metricsMs |> should equal (Some 86_400_000L)

[<Fact>]
let ``Effective for different tenants are independent`` () =
    let store = RetentionStore(defaults, InMemoryRetentionRepo())
    store.SetOverride(tid1, policy (Some 1_000L) None None)
    // tid2 should not be affected
    store.Effective(tid2).metricsMs |> should equal (Some 86_400_000L)

[<Fact>]
let ``SetOverride normalises zero TTL to None (keep forever)`` () =
    let store = RetentionStore(defaults, InMemoryRetentionRepo())
    store.SetOverride(tid1, policy (Some 0L) None None)  // 0 = keep forever
    // All-None after normalisation → removed, falls back to default
    store.Effective(tid1).metricsMs |> should equal (Some 86_400_000L)

// -- EmbeddedCompactor.CompactOnce ------------------------------------------

let private nowMs () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

[<Fact>]
let ``CompactOnce prunes metrics older than the configured TTL`` () =
    let ms     = MetricStore(4096)
    let ttl    = 60_000L                       // 60-second TTL
    let now    = nowMs ()
    let old    = now - 120_000L                // 2 minutes ago → should be pruned
    let recent = now - 10_000L                 // 10 seconds ago → must survive

    ms.Record("series-old",    { ts = old;    value = 1.0 })
    ms.Record("series-recent", { ts = recent; value = 2.0 })

    let retStore = RetentionStore(policy (Some ttl) None None, InMemoryRetentionRepo())
    let compactor = new EmbeddedCompactor(retStore, Some ms, None, 60_000)
    let (metricsDropped, logsDropped) = compactor.CompactOnce()

    metricsDropped |> should be (greaterThan 0)
    logsDropped    |> should equal 0
    // PruneOlderThan removes points but keeps the series key; verify old series has no points
    ms.Get("series-old")    |> should haveLength 0
    ms.Get("series-recent") |> should not' (haveLength 0)

[<Fact>]
let ``CompactOnce prunes log entries older than the configured TTL`` () =
    let ls  = LogStore(4096)
    let ttl = 60_000L
    let now = nowMs ()

    ls.Add { ts = now - 120_000L; service = "svc"; level = "info"; message = "old"    }
    ls.Add { ts = now - 10_000L;  service = "svc"; level = "info"; message = "recent" }

    let retStore  = RetentionStore(policy None (Some ttl) None, InMemoryRetentionRepo())
    let compactor = new EmbeddedCompactor(retStore, None, Some ls, 60_000)
    let (metricsDropped, logsDropped) = compactor.CompactOnce()

    logsDropped    |> should equal 1
    metricsDropped |> should equal 0

[<Fact>]
let ``CompactOnce skips compaction when no TTL is configured`` () =
    let ms = MetricStore(4096)
    ms.Record("old", { ts = 0L; value = 1.0 })

    let retStore  = RetentionStore(noDefaults, InMemoryRetentionRepo())
    let compactor = new EmbeddedCompactor(retStore, Some ms, None, 60_000)
    let (dropped, _) = compactor.CompactOnce()

    dropped |> should equal 0
    ms.Names() |> should haveLength 1

[<Fact>]
let ``CompactOnce uses the most generous TTL across defaults and overrides`` () =
    // Default TTL = 1 minute; one tenant override = 10 minutes.
    // Data 5 minutes old is newer than the generous override, so it survives.
    let ms    = MetricStore(4096)
    let now   = nowMs ()
    ms.Record("series", { ts = now - 300_000L; value = 9.0 })

    let defaultTtl  = 60_000L         // 1 min
    let overrideTtl = 600_000L        // 10 min
    let repo = InMemoryRetentionRepo()
    let retStore = RetentionStore(policy (Some defaultTtl) None None, repo)
    retStore.SetOverride(tid1, policy (Some overrideTtl) None None)

    let compactor = new EmbeddedCompactor(retStore, Some ms, None, 60_000)
    let (dropped, _) = compactor.CompactOnce()

    // Data is 5 min old; the most generous TTL is 10 min → keep it
    dropped |> should equal 0

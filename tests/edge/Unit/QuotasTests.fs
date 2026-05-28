module PulseBoard.Tests.Unit.QuotasTests

open Xunit
open FsUnit.Xunit
open PulseBoard.Quotas
open PulseBoard.Tenancy

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

let private tid  s = TenantId s
let private tidA   = tid "tenant-a"
let private tidB   = tid "tenant-b"

/// QuotaStore with generous defaults and given cardinality cap (0 = unlimited).
let private makeStore card =
    let defaults =
        allKinds
        |> Array.map (fun k -> k, { capacity = 1_000.0; refillPerSec = 1_000.0 })
        |> Map.ofArray
    QuotaStore(defaults, cardinalityDefault = card, repo = InMemoryOverrideRepo())

/// QuotaStore with exactly `cap` tokens per bucket and zero refill.
let private tightStore cap =
    let defaults =
        allKinds
        |> Array.map (fun k -> k, { capacity = float cap; refillPerSec = 0.0 })
        |> Map.ofArray
    QuotaStore(defaults, cardinalityDefault = 0, repo = InMemoryOverrideRepo())

// ---------------------------------------------------------------------------
// Limiter.TryAcquire — basic token-bucket behaviour
// ---------------------------------------------------------------------------

[<Fact>]
let ``TryAcquire returns Ok when bucket has ample tokens`` () =
    let limiter = Limiter(makeStore 0)
    limiter.TryAcquire(tidA, Ingest) |> should equal AcquireResult.Ok

[<Fact>]
let ``TryAcquire exhausts bucket and then returns Throttled`` () =
    let store   = tightStore 2
    let limiter = Limiter(store)
    limiter.TryAcquire(tidA, Ingest) |> should equal AcquireResult.Ok
    limiter.TryAcquire(tidA, Ingest) |> should equal AcquireResult.Ok
    match limiter.TryAcquire(tidA, Ingest) with
    | AcquireResult.Throttled ms -> ms |> should be (greaterThan 0)
    | _ -> failwith "Expected Throttled after bucket exhaustion"

[<Fact>]
let ``TryAcquire on a disabled limit always returns Ok`` () =
    let store = makeStore 0
    store.SetRateOverride(tidA, Ingest, Some disabled)
    let limiter = Limiter(store)
    for _ in 1 .. 10_000 do
        limiter.TryAcquire(tidA, Ingest) |> should equal AcquireResult.Ok

[<Fact>]
let ``TryAcquire tenants do not share the same bucket`` () =
    let store   = tightStore 1
    let limiter = Limiter(store)
    limiter.TryAcquire(tidA, Ingest) |> should equal AcquireResult.Ok
    // tidB has its own full bucket — should still succeed
    limiter.TryAcquire(tidB, Ingest) |> should equal AcquireResult.Ok

[<Fact>]
let ``TryAcquire different Kinds are separate buckets for the same tenant`` () =
    let store   = tightStore 1
    let limiter = Limiter(store)
    limiter.TryAcquire(tidA, Ingest) |> should equal AcquireResult.Ok
    // Query bucket is still full
    limiter.TryAcquire(tidA, Query)  |> should equal AcquireResult.Ok

// ---------------------------------------------------------------------------
// Limiter.TryAdmitSeries — cardinality control
// ---------------------------------------------------------------------------

[<Fact>]
let ``TryAdmitSeries allows new series when no cap is set`` () =
    let limiter = Limiter(makeStore 0)
    for i in 1 .. 1_000 do
        limiter.TryAdmitSeries(tidA, sprintf "metric_%d" i)
        |> should equal CardinalityResult.Ok

[<Fact>]
let ``TryAdmitSeries enforces the cap and rejects the next new series`` () =
    let limiter = Limiter(makeStore 3)
    for i in 1 .. 3 do
        limiter.TryAdmitSeries(tidA, sprintf "m%d" i) |> should equal CardinalityResult.Ok
    match limiter.TryAdmitSeries(tidA, "m4") with
    | CardinalityResult.Rejected 3 -> ()
    | other -> failwith (sprintf "Expected Rejected 3, got %A" other)

[<Fact>]
let ``TryAdmitSeries re-admitting an already-known series is always Ok`` () =
    let limiter = Limiter(makeStore 1)
    limiter.TryAdmitSeries(tidA, "cpu") |> should equal CardinalityResult.Ok
    // Bucket is full (cap=1), but same name -> must still be Ok
    limiter.TryAdmitSeries(tidA, "cpu") |> should equal CardinalityResult.Ok

[<Fact>]
let ``TryAdmitSeries tenants have independent cardinality counters`` () =
    let limiter = Limiter(makeStore 1)
    limiter.TryAdmitSeries(tidA, "cpu") |> should equal CardinalityResult.Ok
    // tidA's bucket is full but tidB starts fresh
    limiter.TryAdmitSeries(tidB, "cpu") |> should equal CardinalityResult.Ok

[<Fact>]
let ``SeriesCountFor reflects admitted series count`` () =
    let limiter = Limiter(makeStore 0)
    limiter.TryAdmitSeries(tidA, "a") |> ignore
    limiter.TryAdmitSeries(tidA, "b") |> ignore
    limiter.TryAdmitSeries(tidA, "a") |> ignore   // duplicate — should not increment
    limiter.SeriesCountFor tidA |> should equal 2

// ---------------------------------------------------------------------------
// QuotaStore — override and default resolution
// ---------------------------------------------------------------------------

[<Fact>]
let ``QuotaStore LimitFor returns the process default when no override is set`` () =
    let store = makeStore 0
    let lim = store.LimitFor(tidA, Ingest)
    lim.capacity     |> should equal 1_000.0
    lim.refillPerSec |> should equal 1_000.0

[<Fact>]
let ``QuotaStore LimitFor returns the per-tenant override when set`` () =
    let store     = makeStore 0
    let newLimit  = { capacity = 42.0; refillPerSec = 7.0 }
    store.SetRateOverride(tidA, Ingest, Some newLimit)
    store.LimitFor(tidA, Ingest) |> should equal newLimit

[<Fact>]
let ``QuotaStore SetRateOverride None reverts to default`` () =
    let store    = makeStore 0
    let newLimit = { capacity = 42.0; refillPerSec = 7.0 }
    store.SetRateOverride(tidA, Ingest, Some newLimit)
    store.SetRateOverride(tidA, Ingest, None)
    store.LimitFor(tidA, Ingest).capacity |> should equal 1_000.0

[<Fact>]
let ``QuotaStore overrides for one tenant do not affect another`` () =
    let store = makeStore 0
    store.SetRateOverride(tidA, Ingest, Some { capacity = 1.0; refillPerSec = 0.0 })
    store.LimitFor(tidB, Ingest).capacity |> should equal 1_000.0

[<Fact>]
let ``QuotaStore CardinalityFor returns the process default when no override is set`` () =
    let store = makeStore 500
    store.CardinalityFor tidA |> should equal 500

[<Fact>]
let ``QuotaStore SetCardinalityOverride overrides per tenant`` () =
    let store = makeStore 500
    store.SetCardinalityOverride(tidA, Some 999)
    store.CardinalityFor tidA |> should equal 999
    store.CardinalityFor tidB |> should equal 500   // default unchanged

[<Fact>]
let ``QuotaStore SetCardinalityOverride None reverts to default`` () =
    let store = makeStore 100
    store.SetCardinalityOverride(tidA, Some 999)
    store.SetCardinalityOverride(tidA, None)
    store.CardinalityFor tidA |> should equal 100

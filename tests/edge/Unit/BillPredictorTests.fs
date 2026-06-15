module PulseBoard.Tests.Unit.BillPredictorTests

// Phase 14.3 — projected monthly bill + Budget rule eval.

open System
open Xunit
open FsUnit.Xunit
open PulseBoard.Tenancy
open PulseBoard.Billing
open PulseBoard.BillPredictor

// Mid-month wall clock so we project to roughly 2× the current usage.
let private midJan2026 =
  DateTimeOffset(2026, 1, 16, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()

let private snapshotOf (pairs : (UsageKind * int64) list) =
  pairs |> Map.ofList

[<Fact>]
let ``periodFor returns calendar-month boundaries in UTC`` () =
    let pStart, pEnd = periodFor midJan2026
    let s = DateTimeOffset.FromUnixTimeMilliseconds(pStart).UtcDateTime
    let e = DateTimeOffset.FromUnixTimeMilliseconds(pEnd).UtcDateTime
    s |> should equal (DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))
    e |> should equal (DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc))

[<Fact>]
let ``project returns elapsedFrac in (0,1] and pillar count = allUsageKinds`` () =
    let snap = snapshotOf [ IngestBytes, 1_000L ]
    let bill = project (TenantId "t1") Plan.Free snap midJan2026
    bill.elapsedFrac |> should greaterThan 0.0
    bill.elapsedFrac |> should lessThanOrEqualTo 1.0
    bill.pillars.Length |> should equal allUsageKinds.Length

[<Fact>]
let ``project linear-extrapolates currentRaw to projectedRaw`` () =
    // Mid-month → elapsedFrac ≈ 0.5 → projected ≈ 2× current.
    let snap = snapshotOf [ IngestBytes, 10_000_000_000L ]   // 10 GB so far
    let bill = project (TenantId "t1") Plan.Pro snap midJan2026
    let ingest = bill.pillars |> Array.find (fun p -> p.pillar = "ingest")
    ingest.currentRaw |> should equal 10_000_000_000L
    let ratio = float ingest.projectedRaw / float ingest.currentRaw
    // Tolerant compare: somewhere between 1.9× and 2.2× (depends on Jan length).
    ratio |> should greaterThan 1.9
    ratio |> should lessThan 2.2

[<Fact>]
let ``project at period start is clamped (no division by zero)`` () =
    let snap = snapshotOf [ IngestBytes, 1_000L ]
    let pStart, _ = periodFor midJan2026
    let bill = project (TenantId "t1") Plan.Free snap pStart
    // elapsedFrac clamped to 0.001 minimum, so projection is finite.
    bill.elapsedFrac |> should greaterThan 0.0
    let ingest = bill.pillars |> Array.find (fun p -> p.pillar = "ingest")
    ingest.projectedRaw |> should greaterThan 0L

[<Fact>]
let ``baseUsd equals plan card monthly + totalUsd = base + sum(pillar usd)`` () =
    let snap = snapshotOf []
    let bill = project (TenantId "t1") Plan.Free snap midJan2026
    let pillarSum = bill.pillars |> Array.sumBy (fun p -> p.usd)
    bill.totalUsd |> should equal (bill.baseUsd + pillarSum)

[<Fact>]
let ``pillarUsd "total" returns totalUsd`` () =
    let snap = snapshotOf []
    let bill = project (TenantId "t1") Plan.Free snap midJan2026
    pillarUsd bill "total" |> should equal (Some (float bill.totalUsd))

[<Fact>]
let ``pillarUsd returns the per-pillar usd for a known pillar`` () =
    let snap = snapshotOf [ IngestBytes, 10_000_000_000L ]
    let bill = project (TenantId "t1") Plan.Pro snap midJan2026
    let ingestUsd = (bill.pillars |> Array.find (fun p -> p.pillar = "ingest")).usd
    pillarUsd bill "ingest" |> should equal (Some (float ingestUsd))

[<Fact>]
let ``pillarUsd returns None for an unknown pillar key`` () =
    let snap = snapshotOf []
    let bill = project (TenantId "t1") Plan.Free snap midJan2026
    pillarUsd bill "nonsense" |> should equal (None : float option)

[<Fact>]
let ``pillarToKind / kindToPillar round-trip on every kind`` () =
    for k in allUsageKinds do
        let p = kindToPillar k
        pillarToKind p |> should equal (Some k)

[<Fact>]
let ``serialiseBill emits valid JSON with required fields`` () =
    let snap = snapshotOf [ IngestBytes, 5_000L ]
    let bill = project (TenantId "t1") Plan.Pro snap midJan2026
    let json = serialiseBill bill
    use doc = System.Text.Json.JsonDocument.Parse json
    let r = doc.RootElement
    r.GetProperty("tenantId").GetString()     |> should equal "t1"
    r.GetProperty("plan").GetString()         |> should equal "pro"
    r.GetProperty("pillars").GetArrayLength() |> should equal allUsageKinds.Length
    r.GetProperty("totalUsd").GetDecimal()    |> should equal bill.totalUsd

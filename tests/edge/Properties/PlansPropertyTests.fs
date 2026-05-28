module PulseBoard.Tests.Properties.PlansPropertyTests

open System
open FsCheck
open FsCheck.Xunit
open PulseBoard.Tenancy
open PulseBoard.Quotas
open PulseBoard.Plans

// ---------------------------------------------------------------------------
// Custom generators
// ---------------------------------------------------------------------------

type AnyPlan = AnyPlan of Plan
type AnyKind = AnyKind of Kind

type PlansGenerators =
    static member AnyPlan() : Arbitrary<AnyPlan> =
        Gen.elements [| Free; Pro; Enterprise |]
        |> Gen.map AnyPlan
        |> Arb.fromGen

    static member AnyKind() : Arbitrary<AnyKind> =
        Gen.elements [| Ingest; Query; AlertEval; LogBytes |]
        |> Gen.map AnyKind
        |> Arb.fromGen

// ---------------------------------------------------------------------------
// allows — Enterprise is always fully entitled
// ---------------------------------------------------------------------------

[<Property(Arbitrary = [| typeof<PlansGenerators> |])>]
let ``allows Enterprise is always true for every feature``
        (feature : Feature) =
    allows Enterprise feature = true

// ---------------------------------------------------------------------------
// defaultRate — structural invariants
// ---------------------------------------------------------------------------

/// Every (plan, kind) pair has a positive burst capacity.
[<Property(Arbitrary = [| typeof<PlansGenerators> |])>]
let ``defaultRate capacity is always positive``
        (AnyPlan plan) (AnyKind kind) =
    (defaultRate plan kind).capacity > 0.0

/// Every (plan, kind) pair has a positive sustained refill rate.
[<Property(Arbitrary = [| typeof<PlansGenerators> |])>]
let ``defaultRate refillPerSec is always positive``
        (AnyPlan plan) (AnyKind kind) =
    (defaultRate plan kind).refillPerSec > 0.0

/// Burst capacity is always at least as large as the per-second refill rate
/// (sustained ≤ burst is a sensible invariant for token-bucket design).
[<Property(Arbitrary = [| typeof<PlansGenerators> |])>]
let ``defaultRate capacity is at least as large as refillPerSec``
        (AnyPlan plan) (AnyKind kind) =
    let l = defaultRate plan kind
    l.capacity >= l.refillPerSec

/// Higher-tier plans have greater-or-equal capacity for every kind
/// (Free ≤ Pro ≤ Enterprise).
[<Property(Arbitrary = [| typeof<PlansGenerators> |])>]
let ``defaultRate capacity is monotone non-decreasing across plan tiers``
        (AnyKind kind) =
    let free       = (defaultRate Free       kind).capacity
    let pro        = (defaultRate Pro        kind).capacity
    let enterprise = (defaultRate Enterprise kind).capacity
    free <= pro && pro <= enterprise

// ---------------------------------------------------------------------------
// toHardCap — arithmetic invariants
// ---------------------------------------------------------------------------

/// toHardCap always returns a value ≥ the soft cap.
[<Property>]
let ``toHardCap is always >= soft cap``
        (PositiveInt raw) =
    let soft = int64 raw
    toHardCap soft >= soft

/// toHardCap of MaxValue is MaxValue (no overflow).
[<Property>]
let ``toHardCap of MaxValue is MaxValue`` () =
    toHardCap Int64.MaxValue = Int64.MaxValue

/// toHardCap is non-decreasing for inputs ≥ 2 (below 2 saturates to MaxValue).
[<Property>]
let ``toHardCap is non-decreasing for inputs above 1``
        (PositiveInt a) (PositiveInt b) =
    let x = max 2L (int64 (min a b))
    let y = max 2L (int64 (max a b))
    toHardCap x <= toHardCap y

// ---------------------------------------------------------------------------
// defaultCardinality — tier ordering
// ---------------------------------------------------------------------------

/// Cardinality limits respect the Free ≤ Pro ≤ Enterprise ordering.
[<Property>]
let ``defaultCardinality is monotone across plan tiers`` () =
    defaultCardinality Free <= defaultCardinality Pro &&
    defaultCardinality Pro  <= defaultCardinality Enterprise

/// All cardinality limits are strictly positive.
[<Property(Arbitrary = [| typeof<PlansGenerators> |])>]
let ``defaultCardinality is always positive``
        (AnyPlan plan) =
    defaultCardinality plan > 0

// ---------------------------------------------------------------------------
// Soft-cap functions — tier ordering invariants
// ---------------------------------------------------------------------------

/// Soft caps are non-negative and respect Free ≤ Pro ≤ Enterprise ordering.
[<Property>]
let ``ingestBytesSoftCap is monotone across plan tiers`` () =
    ingestBytesSoftCap Free <= ingestBytesSoftCap Pro &&
    ingestBytesSoftCap Pro  <= ingestBytesSoftCap Enterprise

[<Property>]
let ``logBytesSoftCap is monotone across plan tiers`` () =
    logBytesSoftCap Free <= logBytesSoftCap Pro &&
    logBytesSoftCap Pro  <= logBytesSoftCap Enterprise

[<Property>]
let ``seatsSoftCap is monotone across plan tiers`` () =
    seatsSoftCap Free <= seatsSoftCap Pro &&
    seatsSoftCap Pro  <= seatsSoftCap Enterprise

/// Hard cap is always ≥ soft cap for all computed soft caps.
[<Property(Arbitrary = [| typeof<PlansGenerators> |])>]
let ``toHardCap of ingestBytesSoftCap is always >= softCap``
        (AnyPlan plan) =
    let soft = ingestBytesSoftCap plan
    toHardCap soft >= soft

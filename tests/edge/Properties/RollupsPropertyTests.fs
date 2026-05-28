module PulseBoard.Tests.Properties.RollupsPropertyTests

open FsCheck
open FsCheck.Xunit
open PulseBoard.Rollups
open PulseBoard.TimeSeries

// ---------------------------------------------------------------------------
// Custom generators
// ---------------------------------------------------------------------------

/// A finite, non-NaN, non-Inf float.
type NormalVal = NormalVal of float

type Generators =
    static member NormalVal() =
        Arb.from<NormalFloat>
        |> Arb.convert (fun (NormalFloat v) -> NormalVal v) (fun (NormalVal v) -> NormalFloat v)

[<assembly: Properties(Arbitrary = [| typeof<Generators> |])>]
do ()

// ---------------------------------------------------------------------------
// aggregate — algebraic invariants
// ---------------------------------------------------------------------------

/// Positive int64 safe for use as a timestamp (avoids overflow in arithmetic).
let private safeTs (i : int) : int64 = int64 (abs i) * 7_777L

[<Property>]
let ``aggregate: sum of bucket sums equals sum of raw values``
        (rawPairs : (PositiveInt * NormalVal) list) =
    let pts =
        rawPairs
        |> List.mapi (fun i (PositiveInt _, NormalVal v) ->
            { ts = safeTs i; value = v })
    let buckets = aggregate 60_000L pts
    let bucketSum = buckets |> Array.sumBy (fun b -> b.sum)
    let rawSum    = pts     |> List.sumBy  (fun p -> p.value)
    abs (bucketSum - rawSum) < 1e-6

[<Property>]
let ``aggregate: sum of bucket counts equals number of input points``
        (rawPairs : (PositiveInt * NormalVal) list) =
    let pts =
        rawPairs
        |> List.mapi (fun i (PositiveInt _, NormalVal v) ->
            { ts = safeTs i; value = v })
    let buckets  = aggregate 60_000L pts
    let bucketN  = buckets |> Array.sumBy (fun b -> b.count)
    bucketN = pts.Length

[<Property>]
let ``aggregate: every bucket min <= max``
        (rawPairs : (PositiveInt * NormalVal) list) =
    let pts =
        rawPairs
        |> List.mapi (fun i (PositiveInt _, NormalVal v) ->
            { ts = safeTs i; value = v })
    let buckets = aggregate 60_000L pts
    buckets |> Array.forall (fun b -> b.min <= b.max)

[<Property>]
let ``aggregate: every bucket avg is between its min and max``
        (rawPairs : (PositiveInt * NormalVal) list) =
    let pts =
        rawPairs
        |> List.mapi (fun i (PositiveInt _, NormalVal v) ->
            { ts = safeTs i; value = v })
    let buckets = aggregate 60_000L pts
    buckets
    |> Array.forall (fun b ->
        b.count = 0
        || (b.Avg >= b.min - 1e-9 && b.Avg <= b.max + 1e-9))

[<Property>]
let ``aggregate: output is sorted by bucket start timestamp``
        (rawPairs : (PositiveInt * NormalVal) list) =
    let pts =
        rawPairs
        |> List.mapi (fun i (PositiveInt _, NormalVal v) ->
            { ts = safeTs i; value = v })
    let buckets = aggregate 60_000L pts
    buckets
    |> Array.pairwise
    |> Array.forall (fun (a, b) -> a.ts < b.ts)

// ---------------------------------------------------------------------------
// RollupStore — roundtrip property
// ---------------------------------------------------------------------------

[<Property>]
let ``RollupStore Replace then Get is a no-op when maxBuckets >= array length``
        (buckets : NormalVal list) =
    let bs =
        buckets
        |> List.mapi (fun i (NormalVal v) ->
            { ts = int64 i * 60_000L; count = 1; min = v; max = v; sum = v })
        |> Array.ofList
    let store = RollupStore(max 1 bs.Length)
    store.Replace("m", 60_000L, bs)
    let got = store.Get("m", 60_000L)
    got.Length = bs.Length

// ---------------------------------------------------------------------------
// bucketStart — monotonicity
// ---------------------------------------------------------------------------

[<Property>]
let ``bucketStart output is always <= input timestamp``
        (ts : PositiveInt) =
    let tsMs = int64 (abs (ts.Get))
    bucketStart tsMs 60_000L <= tsMs

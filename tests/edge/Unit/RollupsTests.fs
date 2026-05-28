module PulseBoard.Tests.Unit.RollupsTests

open Xunit
open FsUnit.Xunit
open PulseBoard.Rollups
open PulseBoard.TimeSeries

// Helpers
let private pt ts v : Point = { ts = ts; value = v }

// ---------------------------------------------------------------------------
// bucketStart
// ---------------------------------------------------------------------------

[<Fact>]
let ``bucketStart aligns ts below a boundary to the boundary below it`` () =
    bucketStart 90_000L 60_000L |> should equal 60_000L

[<Fact>]
let ``bucketStart on an exact boundary is unchanged`` () =
    bucketStart 60_000L 60_000L |> should equal 60_000L

[<Fact>]
let ``bucketStart at epoch zero is zero`` () =
    bucketStart 0L 60_000L |> should equal 0L

[<Fact>]
let ``bucketStart uses the correct resolution for 5-minute buckets`` () =
    // 7 minutes = 420 000 ms -> bucket start at 5 min = 300 000 ms
    bucketStart 420_000L 300_000L |> should equal 300_000L

// ---------------------------------------------------------------------------
// aggregate — grouping
// ---------------------------------------------------------------------------

[<Fact>]
let ``aggregate returns empty for empty input`` () =
    aggregate 60_000L (Seq.empty) |> should be Empty

[<Fact>]
let ``aggregate single point produces one bucket`` () =
    let buckets = aggregate 60_000L [| pt 30_000L 5.0 |]
    buckets.Length |> should equal 1
    buckets.[0].ts    |> should equal 0L
    buckets.[0].count |> should equal 1
    buckets.[0].sum   |> should equal 5.0
    buckets.[0].min   |> should equal 5.0
    buckets.[0].max   |> should equal 5.0

[<Fact>]
let ``aggregate groups two points in same bucket`` () =
    let buckets = aggregate 60_000L [| pt 10_000L 2.0; pt 50_000L 4.0 |]
    buckets.Length    |> should equal 1
    buckets.[0].count |> should equal 2
    buckets.[0].sum   |> should equal 6.0
    buckets.[0].min   |> should equal 2.0
    buckets.[0].max   |> should equal 4.0

[<Fact>]
let ``aggregate separates points in different buckets`` () =
    let buckets = aggregate 60_000L [| pt 30_000L 2.0; pt 90_000L 10.0 |]
    buckets.Length |> should equal 2

[<Fact>]
let ``aggregate output is sorted ascending by bucket start`` () =
    let buckets =
        aggregate 60_000L [| pt 120_000L 1.0; pt 30_000L 2.0; pt 90_000L 3.0 |]
    buckets |> Array.pairwise |> Array.iter (fun (a, b) ->
        a.ts |> should be (lessThan b.ts))

// ---------------------------------------------------------------------------
// aggregate — arithmetic
// ---------------------------------------------------------------------------

[<Fact>]
let ``aggregate avg equals sum divided by count`` () =
    let buckets = aggregate 60_000L [| pt 10_000L 3.0; pt 20_000L 7.0 |]
    buckets.[0].Avg |> should (equalWithin 1e-9) 5.0

[<Fact>]
let ``aggregate min and max track correctly across many points`` () =
    let points = [| pt 0L 5.0; pt 1_000L 1.0; pt 2_000L 9.0; pt 3_000L 3.0 |]
    let buckets = aggregate 60_000L points
    buckets.[0].min |> should equal 1.0
    buckets.[0].max |> should equal 9.0

[<Fact>]
let ``Bucket Value returns correct aggregation for each Agg case`` () =
    let b = { ts = 0L; count = 4; min = 1.0; max = 7.0; sum = 16.0 }
    b.Value Agg.Avg   |> should (equalWithin 1e-9) 4.0
    b.Value Agg.Min   |> should equal 1.0
    b.Value Agg.Max   |> should equal 7.0
    b.Value Agg.Sum   |> should equal 16.0
    b.Value Agg.Count |> should equal 4.0

// ---------------------------------------------------------------------------
// tryParseAgg
// ---------------------------------------------------------------------------

[<Theory>]
[<InlineData("avg",   "avg")>]
[<InlineData("",      "avg")>]
[<InlineData("min",   "min")>]
[<InlineData("max",   "max")>]
[<InlineData("sum",   "sum")>]
[<InlineData("count", "count")>]
let ``tryParseAgg parses known strings`` (input : string) (expected : string) =
    let agg = tryParseAgg input |> Option.get
    agg.Name |> should equal expected

[<Fact>]
let ``tryParseAgg returns None for unknown string`` () =
    tryParseAgg "median" |> should equal None

// ---------------------------------------------------------------------------
// RollupStore
// ---------------------------------------------------------------------------

[<Fact>]
let ``RollupStore Get returns empty for unknown series`` () =
    let store = RollupStore(1000)
    store.Get("nonexistent", 60_000L) |> should be Empty

[<Fact>]
let ``RollupStore Replace then Get roundtrips correctly`` () =
    let store = RollupStore(1000)
    let bs = [| { ts = 0L; count = 5; min = 1.0; max = 9.0; sum = 25.0 } |]
    store.Replace("cpu", 60_000L, bs)
    let got = store.Get("cpu", 60_000L)
    got.Length    |> should equal 1
    got.[0].sum   |> should equal 25.0
    got.[0].count |> should equal 5

[<Fact>]
let ``RollupStore Get returns empty when resolution does not match`` () =
    let store = RollupStore(1000)
    let bs = [| { ts = 0L; count = 1; min = 1.0; max = 1.0; sum = 1.0 } |]
    store.Replace("cpu", 60_000L, bs)
    store.Get("cpu", 300_000L) |> should be Empty

[<Fact>]
let ``RollupStore Replace trims to maxBucketsPerSeries keeping most recent`` () =
    let store = RollupStore(3)
    let bs =
        Array.init 10 (fun i ->
            { ts = int64 i * 60_000L; count = 1; min = 1.0; max = 1.0; sum = 1.0 })
    store.Replace("cpu", 60_000L, bs)
    let got = store.Get("cpu", 60_000L)
    got.Length   |> should equal 3
    got.[0].ts   |> should equal 420_000L
    got.[2].ts   |> should equal 540_000L

[<Fact>]
let ``RollupStore GetSince filters out buckets before the cutoff`` () =
    let store = RollupStore(1000)
    let bs =
        Array.init 5 (fun i ->
            { ts = int64 i * 60_000L; count = 1; min = 1.0; max = 1.0; sum = float i })
    store.Replace("cpu", 60_000L, bs)
    let got = store.GetSince("cpu", 60_000L, 120_000L)
    got |> Array.forall (fun b -> b.ts >= 120_000L) |> should be True
    got.Length |> should equal 3

[<Fact>]
let ``RollupStore GetSinceAgg materialises point array with correct values`` () =
    let store = RollupStore(1000)
    let bs =
        [| { ts = 0L; count = 2; min = 3.0; max = 7.0; sum = 10.0 } |]
    store.Replace("latency", 60_000L, bs)
    let pts = store.GetSinceAgg("latency", 60_000L, 0L, Agg.Avg)
    pts.Length    |> should equal 1
    pts.[0].value |> should (equalWithin 1e-9) 5.0

[<Fact>]
let ``RollupStore different series are stored independently`` () =
    let store = RollupStore(1000)
    store.Replace("cpu",    60_000L, [| { ts = 0L; count = 1; min = 1.0; max = 1.0; sum = 1.0 } |])
    store.Replace("memory", 60_000L, [| { ts = 0L; count = 1; min = 2.0; max = 2.0; sum = 2.0 } |])
    store.Get("cpu",    60_000L).[0].sum |> should equal 1.0
    store.Get("memory", 60_000L).[0].sum |> should equal 2.0

[<Fact>]
let ``RollupStore different resolutions for same series are stored independently`` () =
    let store = RollupStore(1000)
    store.Replace("cpu", Resolution.OneMinute.Ms,   [| { ts = 0L; count = 1; min = 1.0; max = 1.0; sum = 1.0 } |])
    store.Replace("cpu", Resolution.FiveMinutes.Ms, [| { ts = 0L; count = 5; min = 1.0; max = 5.0; sum = 15.0 } |])
    store.Get("cpu", Resolution.OneMinute.Ms  ).[0].count |> should equal 1
    store.Get("cpu", Resolution.FiveMinutes.Ms).[0].count |> should equal 5

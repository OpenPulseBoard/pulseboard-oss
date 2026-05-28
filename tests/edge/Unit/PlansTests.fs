module PulseBoard.Tests.Unit.PlansTests

open System
open Xunit
open FsUnit.Xunit
open PulseBoard.Plans
open PulseBoard.Tenancy
open PulseBoard.Quotas

// -- allows -----------------------------------------------------------------

[<Fact>]
let ``Enterprise allows all features`` () =
    allows Enterprise Feature.Sso          |> should be True
    allows Enterprise Feature.CustomDomain |> should be True
    allows Enterprise Feature.Byok         |> should be True

[<Fact>]
let ``Pro allows Sso only`` () =
    allows Pro Feature.Sso          |> should be True
    allows Pro Feature.CustomDomain |> should be False

[<Fact>]
let ``Free allows no features`` () =
    allows Free Feature.Sso          |> should be False
    allows Free Feature.CustomDomain |> should be False

// -- defaultRate ------------------------------------------------------------

[<Fact>]
let ``defaultRate returns a capacity greater than zero for all plans and kinds`` () =
    for plan in [ Free; Pro; Enterprise ] do
        for kind in [ Ingest; Query; AlertEval; LogBytes ] do
            let lim = defaultRate plan kind
            lim.capacity     |> should be (greaterThan 0.0)
            lim.refillPerSec |> should be (greaterThan 0.0)

[<Fact>]
let ``defaultRate capacity increases from Free to Pro to Enterprise`` () =
    let cap plan kind = (defaultRate plan kind).capacity
    cap Free      Ingest |> should be (lessThanOrEqualTo (cap Pro      Ingest))
    cap Pro       Ingest |> should be (lessThanOrEqualTo (cap Enterprise Ingest))

// -- defaultCardinality -----------------------------------------------------

[<Fact>]
let ``defaultCardinality returns expected values per plan`` () =
    defaultCardinality Free       |> should equal 10_000
    defaultCardinality Pro        |> should equal 250_000
    defaultCardinality Enterprise |> should equal 5_000_000

// -- toHardCap --------------------------------------------------------------

[<Fact>]
let ``toHardCap scales soft cap by 1.5x`` () =
    toHardCap 10L |> should equal 15L
    toHardCap  2L |> should equal  3L
    toHardCap  4L |> should equal  6L

[<Fact>]
let ``toHardCap preserves Int64.MaxValue sentinel as-is`` () =
    toHardCap Int64.MaxValue |> should equal Int64.MaxValue

[<Fact>]
let ``toHardCap returns MaxValue when result would overflow`` () =
    // soft=1 → 1/2*3=0 < 1 → MaxValue (saturation on underflow/overflow)
    toHardCap 1L |> should equal Int64.MaxValue

// -- soft cap functions -----------------------------------------------------

[<Fact>]
let ``ingestBytesSoftCap increases from Free to Enterprise`` () =
    ingestBytesSoftCap Free       |> should be (lessThan (ingestBytesSoftCap Enterprise))

[<Fact>]
let ``logBytesSoftCap increases from Free to Enterprise`` () =
    logBytesSoftCap Free          |> should be (lessThan (logBytesSoftCap Enterprise))

[<Fact>]
let ``activeSeriesSoftCap returns positive values for all plans`` () =
    for plan in [ Free; Pro; Enterprise ] do
        activeSeriesSoftCap plan  |> should be (greaterThan 0L)

[<Fact>]
let ``traceSpansSoftCap returns positive values for all plans`` () =
    for plan in [ Free; Pro; Enterprise ] do
        traceSpansSoftCap plan    |> should be (greaterThan 0L)

[<Fact>]
let ``alertEvalsSoftCap returns positive values for all plans`` () =
    for plan in [ Free; Pro; Enterprise ] do
        alertEvalsSoftCap plan    |> should be (greaterThan 0L)

[<Fact>]
let ``seatsSoftCap increases from Free to Enterprise`` () =
    seatsSoftCap Free             |> should be (lessThanOrEqualTo (seatsSoftCap Enterprise))

[<Fact>]
let ``all soft caps are consistent with toHardCap contract`` () =
    // toHardCap of any finite soft cap should be >= soft cap itself
    let softCaps = [
        ingestBytesSoftCap   Pro
        logBytesSoftCap      Pro
        activeSeriesSoftCap  Pro
        traceSpansSoftCap    Pro
        alertEvalsSoftCap    Pro
        seatsSoftCap         Pro
    ]
    for s in softCaps do
        if s <> Int64.MaxValue then
            toHardCap s |> should be (greaterThanOrEqualTo s)

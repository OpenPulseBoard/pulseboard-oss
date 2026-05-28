module PulseBoard.Tests.Unit.SpansTests

open System
open Xunit
open FsUnit.Xunit
open PulseBoard.Spans
open PulseBoard.Tenancy

// -- helpers ----------------------------------------------------------------

let private tid = TenantId "t1"

/// parentSpanId is "" for root spans (not option).
let private mkSpan traceId spanId (parentSpanId : string) service op startMs endMs statusCode : Span =
    { traceId      = traceId
      spanId       = spanId
      parentSpanId = parentSpanId
      service      = service
      operation    = op
      kind         = KindServer
      startMs      = startMs
      endMs        = endMs
      statusCode   = statusCode
      attributes   = Map.empty }

let private root trId sid svc op startMs endMs sc =
    mkSpan trId sid "" svc op startMs endMs sc

let private child trId sid pid svc op startMs endMs sc =
    mkSpan trId sid pid svc op startMs endMs sc

// -- duration ---------------------------------------------------------------

[<Fact>]
let ``duration returns endMs minus startMs`` () =
    let s = root "t" "s" "svc" "op" 1000L 4000L 0
    duration s |> should equal 3000L

[<Fact>]
let ``duration returns zero when endMs equals startMs`` () =
    let s = root "t" "s" "svc" "op" 1000L 1000L 0
    duration s |> should equal 0L

[<Fact>]
let ``duration clamps to zero when endMs is before startMs`` () =
    let s = root "t" "s" "svc" "op" 2000L 1000L 0
    duration s |> should equal 0L

// -- isError ----------------------------------------------------------------

[<Fact>]
let ``isError returns true for statusCode 2 (OTLP error)`` () =
    root "t" "s" "svc" "op" 0L 0L 2 |> isError |> should be True

[<Fact>]
let ``isError returns false for statusCode 0 (unset)`` () =
    root "t" "s" "svc" "op" 0L 0L 0 |> isError |> should be False

[<Fact>]
let ``isError returns false for statusCode 1 (ok)`` () =
    root "t" "s" "svc" "op" 0L 0L 1 |> isError |> should be False

// -- summarise --------------------------------------------------------------

[<Fact>]
let ``summarise identifies the root span as the one without a parent in the trace`` () =
    let r = root  "tr" "root" "api"    "GET /"   0L 100L 0
    let c = child "tr" "c1"  "root" "db" "query" 10L 90L 0
    let s = summarise [| r; c |]
    s.rootService |> should equal "api"

[<Fact>]
let ``summarise counts total spans in the trace`` () =
    let r = root  "tr" "root" "a" "op" 0L 10L 0
    let c = child "tr" "c1"  "root" "b" "op" 1L 9L 0
    summarise [| r; c |] |> fun s -> s.spanCount |> should equal 2

[<Fact>]
let ``summarise counts errors correctly`` () =
    let r = root  "tr" "root" "a" "op" 0L 10L 0
    let c = child "tr" "c1"  "root" "b" "op" 1L 9L 2
    summarise [| r; c |] |> fun s -> s.errorCount |> should equal 1

[<Fact>]
let ``summarise captures the services involved in the trace`` () =
    let r = root  "tr" "root" "api"    "GET /" 0L 100L 0
    let c = child "tr" "c1"  "root" "db"  "query" 10L 90L 0
    let s = summarise [| r; c |]
    s.services |> Array.sort |> should equal [| "api"; "db" |]

[<Fact>]
let ``summarise trace duration equals latest endMs minus earliest startMs`` () =
    let r = root  "tr" "root" "a" "op" 0L   100L 0
    let c = child "tr" "c1"  "root" "b" "op" 50L 200L 0
    summarise [| r; c |] |> fun s -> s.durationMs |> should equal 200L

// -- buildMap ---------------------------------------------------------------

[<Fact>]
let ``buildMap includes a node for every distinct service`` () =
    let r = root  "tr" "r" "api" "GET /" 0L 100L 0
    let c = child "tr" "c" "r" "db"  "query" 10L 90L 0
    let m = buildMap [| r; c |] 0L
    m.nodes |> Array.map (fun n -> n.service) |> Array.sort
             |> should equal [| "api"; "db" |]

[<Fact>]
let ``buildMap creates an edge when parent and child service differ`` () =
    let r = root  "tr" "r" "api" "op" 0L 100L 0
    let c = child "tr" "c" "r" "db"  "op" 10L 90L 0
    let m = buildMap [| r; c |] 0L
    m.edges |> should haveLength 1
    m.edges.[0].fromService |> should equal "api"
    m.edges.[0].toService   |> should equal "db"

[<Fact>]
let ``buildMap does not create an edge when parent and child share the same service`` () =
    let r = root  "tr" "r" "api" "op1" 0L 100L 0
    let c = child "tr" "c" "r" "api" "op2" 10L 90L 0
    let m = buildMap [| r; c |] 0L
    m.edges |> should haveLength 0

[<Fact>]
let ``buildMap counts error spans per service`` () =
    let r = root  "tr" "r" "api" "op" 0L 100L 2  // error
    let m = buildMap [| r |] 0L
    (m.nodes |> Array.find (fun n -> n.service = "api")).errorCount
    |> should equal 1

[<Fact>]
let ``buildMap respects sinceMs filter — excludes spans older than cutoff`` () =
    let old    = root "t1" "r1" "api" "op" 0L    50L   0
    let recent = root "t2" "r2" "api" "op" 5000L 5100L 0
    let cutoff = 1000L
    // buildMap takes pre-filtered spans; caller is responsible for filtering (see InMemorySpanStore.Map)
    let filtered = [| old; recent |] |> Array.filter (fun s -> s.endMs >= cutoff)
    let m = buildMap filtered cutoff
    m.nodes.[0].spanCount |> should equal 1

// -- InMemorySpanStore -------------------------------------------------------

[<Fact>]
let ``InMemorySpanStore Count returns 0 for fresh store`` () =
    let store = InMemorySpanStore(100)
    (store :> ISpanStore).Count tid |> should equal 0

[<Fact>]
let ``InMemorySpanStore Count increments after Ingest`` () =
    let store = InMemorySpanStore(100)
    let s = root "tr1" "s1" "svc" "op" 0L 10L 0
    (store :> ISpanStore).Ingest(tid, [| s |])
    (store :> ISpanStore).Count tid |> should equal 1

[<Fact>]
let ``InMemorySpanStore GetTrace returns empty array for unknown trace`` () =
    let store = InMemorySpanStore(100)
    (store :> ISpanStore).GetTrace(tid, "no-such-trace") |> should haveLength 0

[<Fact>]
let ``InMemorySpanStore GetTrace returns spans after Ingest`` () =
    let store = InMemorySpanStore(100)
    let s = root "tr1" "s1" "svc" "op" 0L 10L 0
    (store :> ISpanStore).Ingest(tid, [| s |])
    (store :> ISpanStore).GetTrace(tid, "tr1") |> should haveLength 1

[<Fact>]
let ``InMemorySpanStore Traces returns recent trace summaries`` () =
    let store = InMemorySpanStore(100)
    let now   = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    let s = root "tr-new" "s1" "svc" "op" now (now + 50L) 0
    (store :> ISpanStore).Ingest(tid, [| s |])
    let traces = (store :> ISpanStore).Traces(tid, now - 1000L, 10)
    traces |> should haveLength 1
    traces.[0].rootService |> should equal "svc"

[<Fact>]
let ``InMemorySpanStore PruneOlderThan removes old spans and returns count`` () =
    let store = InMemorySpanStore(100)
    let old    = root "t-old"    "s1" "svc" "op" 0L        1L        0
    let recent = root "t-recent" "s2" "svc" "op" 1_000_000L 1_000_100L 0
    (store :> ISpanStore).Ingest(tid, [| old    |])
    (store :> ISpanStore).Ingest(tid, [| recent |])
    let dropped = (store :> ISpanStore).PruneOlderThan 500_000L
    dropped |> should be (greaterThan 0)
    (store :> ISpanStore).GetTrace(tid, "t-old")    |> should haveLength 0
    (store :> ISpanStore).GetTrace(tid, "t-recent") |> should haveLength 1

[<Fact>]
let ``InMemorySpanStore Map returns a service map`` () =
    let store = InMemorySpanStore(100)
    let r = root  "tr" "r" "api" "op" 0L 100L 0
    let c = child "tr" "c" "r" "db"  "op" 10L 90L 0
    (store :> ISpanStore).Ingest(tid, [| r; c |])
    let sm = (store :> ISpanStore).Map(tid, 0L)
    sm.nodes |> should haveLength 2
    sm.edges |> should haveLength 1

module PulseBoard.Tests.Unit.QueryApiTests

open Xunit
open FsUnit.Xunit
open PulseBoard.QueryApi
open PulseBoard.TimeSeries

// Helpers ------------------------------------------------------------------

let private store (samples : (string * (int64 * float) seq) seq) : MetricStore =
  let s = MetricStore(1024)
  for (name, pts) in samples do
    for (ts, v) in pts do
      s.Record(name, { ts = ts; value = v })
  s

let private okExpr (q : string) =
  match parsePromExpr q with
  | Result.Ok e -> e
  | Result.Error msg -> failwithf "parse failed for %s: %s" q msg

let private vecVal (v : EvalVal) =
  match v with
  | VVector ss -> ss
  | VScalar _  -> failwith "expected vector, got scalar"

let private scalarVal (v : EvalVal) =
  match v with
  | VScalar x -> x
  | VVector _ -> failwith "expected scalar, got vector"

let private valueOf (label : string) (target : string) (ss : PromSeries[]) =
  ss
  |> Array.tryFind (fun s ->
      s.labels |> Array.exists (fun (k, v) -> k = label && v = target))
  |> Option.map (fun s -> s.value)

// =========================================================================
// parsePromExpr — accepted syntax
// =========================================================================

[<Fact>]
let ``parses bare metric name`` () =
  match parsePromExpr "foo" with
  | Result.Ok (PeSel sel) -> sel.name |> should equal (Some "foo")
  | _ -> failwith "expected vector selector"

[<Fact>]
let ``parses metric with label matchers`` () =
  match parsePromExpr """foo{a="b", c!~"d"}""" with
  | Result.Ok (PeSel sel) ->
    sel.matchers.Length |> should equal 2
  | _ -> failwith "expected vector selector"

[<Fact>]
let ``parses range selector inside rate`` () =
  match parsePromExpr "rate(foo[5m])" with
  | Result.Ok (PeCall ("rate", PeRange (_, ms))) ->
    ms |> should equal 300_000L
  | _ -> failwith "expected rate(...[5m])"

[<Fact>]
let ``parses scalar arithmetic`` () =
  match parsePromExpr "100 - 25 * 2" with
  | Result.Ok (PeBin (PoSub, PeNum 100.0, PeBin (PoMul, PeNum 25.0, PeNum 2.0))) -> ()
  | other -> failwithf "unexpected AST: %A" other

[<Fact>]
let ``parses parenthesised expression`` () =
  match parsePromExpr "(1 + 2) * 3" with
  | Result.Ok (PeBin (PoMul, PeBin (PoAdd, PeNum 1.0, PeNum 2.0), PeNum 3.0)) -> ()
  | other -> failwithf "unexpected AST: %A" other

[<Fact>]
let ``parses unary minus`` () =
  match parsePromExpr "-5" with
  | Result.Ok (PeNeg (PeNum 5.0)) -> ()
  | other -> failwithf "unexpected AST: %A" other

[<Fact>]
let ``parses aggregation`` () =
  match parsePromExpr "sum(foo)" with
  | Result.Ok (PeAggr ("sum", None, PeSel _)) -> ()
  | other -> failwithf "unexpected AST: %A" other

[<Fact>]
let ``parses aggregation with by grouping`` () =
  match parsePromExpr "avg by (instance) (foo)" with
  | Result.Ok (PeAggr ("avg", Some { without = false; labels = [| "instance" |] }, PeSel _)) -> ()
  | other -> failwithf "unexpected AST: %A" other

[<Fact>]
let ``parses aggregation with without grouping`` () =
  match parsePromExpr "sum without (mode, cpu) (foo)" with
  | Result.Ok (PeAggr ("sum", Some { without = true; labels = [| "mode"; "cpu" |] }, PeSel _)) -> ()
  | other -> failwithf "unexpected AST: %A" other

[<Fact>]
let ``parses aggregation with trailing grouping`` () =
  match parsePromExpr "sum (foo) by (instance)" with
  | Result.Ok (PeAggr ("sum", Some { without = false; labels = [| "instance" |] }, PeSel _)) -> ()
  | other -> failwithf "unexpected AST: %A" other

[<Fact>]
let ``parses Linux Host CPU dashboard expression`` () =
  parsePromExpr """100 - avg(rate(node_cpu_seconds_total{mode="idle"}[5m])) * 100"""
  |> function
     | Result.Ok _ -> ()
     | Result.Error e -> failwithf "expected success, got: %s" e

[<Fact>]
let ``parses memory percent expression`` () =
  parsePromExpr "(1 - node_memory_MemAvailable_bytes / node_memory_MemTotal_bytes) * 100"
  |> function
     | Result.Ok _ -> ()
     | Result.Error e -> failwithf "expected success, got: %s" e

// =========================================================================
// parsePromExpr — rejected syntax
// =========================================================================

[<Fact>]
let ``rejects empty query`` () =
  match parsePromExpr "" with
  | Result.Error _ -> ()
  | Result.Ok _ -> failwith "expected error for empty input"

[<Fact>]
let ``rejects unknown function`` () =
  match parsePromExpr "histogram_quantile(0.9, foo)" with
  | Result.Error msg -> msg |> should haveSubstring "does not support function"
  | Result.Ok _ -> failwith "expected error"

[<Fact>]
let ``rejects rate without range selector`` () =
  match parsePromExpr "rate(foo)" with
  | Result.Error msg -> msg |> should haveSubstring "range selector"
  | Result.Ok _ -> failwith "expected error"

[<Fact>]
let ``rejects trailing garbage`` () =
  match parsePromExpr "foo bar baz" with
  | Result.Error _ -> ()
  | Result.Ok _ -> failwith "expected error"

// =========================================================================
// Evaluator — instant vectors
// =========================================================================

[<Fact>]
let ``evalAt returns most recent sample within stale window`` () =
  let s = store [ "foo", [ 0L, 1.0; 30_000L, 7.0 ] ]
  let v = evalAt s 60_000L (okExpr "foo") |> vecVal
  v.Length |> should equal 1
  v.[0].value |> should equal 7.0

[<Fact>]
let ``evalAt drops samples older than stale window`` () =
  // staleness is 5 minutes; sample is 6 minutes old.
  let s = store [ "foo", [ 0L, 1.0 ] ]
  let v = evalAt s 360_001L (okExpr "foo") |> vecVal
  v |> should be Empty

[<Fact>]
let ``evalAt selects by label matcher`` () =
  let s =
    store [
      "node_cpu_seconds_total{mode=\"idle\"}", [ 0L, 100.0 ]
      "node_cpu_seconds_total{mode=\"user\"}", [ 0L, 50.0 ]
    ]
  let v =
    evalAt s 10_000L (okExpr """node_cpu_seconds_total{mode="idle"}""")
    |> vecVal
  v.Length |> should equal 1
  v |> valueOf "mode" "idle" |> should equal (Some 100.0)

// =========================================================================
// Evaluator — rate / irate / increase
// =========================================================================

[<Fact>]
let ``rate computes per-second delta over the window`` () =
  // 60 increase over 60 seconds = 1.0/s
  let s = store [ "c", [ 0L, 0.0; 30_000L, 30.0; 60_000L, 60.0 ] ]
  let v = evalAt s 60_000L (okExpr "rate(c[60s])") |> vecVal
  v.Length |> should equal 1
  v.[0].value |> should (equalWithin 1e-9) 1.0

[<Fact>]
let ``rate is counter-reset aware`` () =
  // First sample at ts=1 so the window filter (ts > startTs) keeps all three.
  // 1 -> 50 -> reset -> 10. Sum of positive deltas + reset value = 50 + 10 = 60
  // over ~60 s.
  let s = store [ "c", [ 1L, 0.0; 30_000L, 50.0; 60_000L, 10.0 ] ]
  let v = evalAt s 60_000L (okExpr "rate(c[60s])") |> vecVal
  v.[0].value |> should (equalWithin 1e-3) 1.0

[<Fact>]
let ``rate drops the __name__ label`` () =
  let s = store [ "c{host=\"a\"}", [ 0L, 0.0; 30_000L, 30.0; 60_000L, 60.0 ] ]
  let v = evalAt s 60_000L (okExpr "rate(c[60s])") |> vecVal
  v.[0].labels |> Array.exists (fun (k, _) -> k = "__name__") |> should equal false
  v.[0].labels |> Array.exists (fun (k, v) -> k = "host" && v = "a") |> should equal true

[<Fact>]
let ``increase returns rate scaled by window`` () =
  // rate of 1/s over 60 s window = 60 increase.
  let s = store [ "c", [ 0L, 0.0; 30_000L, 30.0; 60_000L, 60.0 ] ]
  let v = evalAt s 60_000L (okExpr "increase(c[60s])") |> vecVal
  v.[0].value |> should (equalWithin 1e-9) 60.0

[<Fact>]
let ``irate uses only the last two samples`` () =
  // last two samples: (30s, 30) -> (60s, 90) -> delta 60 / 30 s = 2.0
  let s = store [ "c", [ 0L, 0.0; 30_000L, 30.0; 60_000L, 90.0 ] ]
  let v = evalAt s 60_000L (okExpr "irate(c[60s])") |> vecVal
  v.[0].value |> should (equalWithin 1e-9) 2.0

// =========================================================================
// Evaluator — aggregations and arithmetic
// =========================================================================

[<Fact>]
let ``avg collapses vector to one labelless series`` () =
  let s =
    store [
      "cpu{mode=\"idle\"}", [ 0L, 10.0 ]
      "cpu{mode=\"user\"}", [ 0L, 30.0 ]
    ]
  let v = evalAt s 10_000L (okExpr "avg(cpu)") |> vecVal
  v.Length |> should equal 1
  v.[0].labels |> should be Empty
  v.[0].value |> should (equalWithin 1e-9) 20.0

[<Fact>]
let ``sum adds the samples`` () =
  let s =
    store [
      "x{i=\"1\"}", [ 0L, 1.0 ]
      "x{i=\"2\"}", [ 0L, 2.0 ]
      "x{i=\"3\"}", [ 0L, 3.0 ]
    ]
  let v = evalAt s 10_000L (okExpr "sum(x)") |> vecVal
  v.[0].value |> should equal 6.0

[<Fact>]
let ``count returns the series count`` () =
  let s =
    store [
      "x{i=\"1\"}", [ 0L, 1.0 ]
      "x{i=\"2\"}", [ 0L, 2.0 ]
    ]
  let v = evalAt s 10_000L (okExpr "count(x)") |> vecVal
  v.[0].value |> should equal 2.0

[<Fact>]
let ``avg by retains the grouping label and groups per value`` () =
  let s =
    store [
      "cpu{instance=\"h1\",mode=\"idle\"}", [ 0L, 10.0 ]
      "cpu{instance=\"h1\",mode=\"user\"}", [ 0L, 30.0 ]
      "cpu{instance=\"h2\",mode=\"idle\"}", [ 0L, 80.0 ]
      "cpu{instance=\"h2\",mode=\"user\"}", [ 0L, 100.0 ]
    ]
  let v = evalAt s 10_000L (okExpr "avg by (instance) (cpu)") |> vecVal
  v.Length |> should equal 2
  v |> valueOf "instance" "h1" |> should equal (Some 20.0)
  v |> valueOf "instance" "h2" |> should equal (Some 90.0)
  // grouping keeps only the `by` label (mode is dropped)
  v.[0].labels |> Array.exists (fun (k, _) -> k = "mode") |> should equal false

[<Fact>]
let ``sum without drops only the named labels`` () =
  let s =
    store [
      "x{instance=\"h1\",mode=\"idle\"}", [ 0L, 1.0 ]
      "x{instance=\"h1\",mode=\"user\"}", [ 0L, 2.0 ]
      "x{instance=\"h2\",mode=\"idle\"}", [ 0L, 4.0 ]
    ]
  let v = evalAt s 10_000L (okExpr "sum without (mode) (x)") |> vecVal
  v.Length |> should equal 2
  v |> valueOf "instance" "h1" |> should equal (Some 3.0)
  v |> valueOf "instance" "h2" |> should equal (Some 4.0)

[<Fact>]
let ``avg by (instance) over rate keeps per-instance CPU`` () =
  // h1 idle rises slowly (rate 0.05/s -> CPU 95); h2 idle rises fast
  // (rate ~0.983/s -> CPU ~1.667). by(instance) must keep them separate.
  let s =
    store [
      "node_cpu_seconds_total{mode=\"idle\",instance=\"h1\"}", [ 0L, 1000.0; 60_000L, 1003.0 ]
      "node_cpu_seconds_total{mode=\"idle\",instance=\"h2\"}", [ 0L, 1000.0; 60_000L, 1059.0 ]
    ]
  let expr =
    okExpr """100 - avg by (instance) (rate(node_cpu_seconds_total{mode="idle"}[5m])) * 100"""
  let v = evalAt s 60_000L expr |> vecVal
  v.Length |> should equal 2
  (v |> valueOf "instance" "h1" |> Option.get) |> should (equalWithin 1e-6) 95.0
  (v |> valueOf "instance" "h2" |> Option.get) |> should (equalWithin 1e-4) 1.666666667

[<Fact>]
let ``scalar binop on scalars returns a scalar`` () =
  let s = MetricStore(16)
  evalAt s 0L (okExpr "100 - 25") |> scalarVal |> should equal 75.0

[<Fact>]
let ``scalar minus vector applies per-sample`` () =
  let s = store [ "cpu", [ 0L, 30.0 ] ]
  let v = evalAt s 10_000L (okExpr "100 - cpu") |> vecVal
  v.[0].value |> should equal 70.0

[<Fact>]
let ``vector divided by scalar applies per-sample`` () =
  let s = store [ "x", [ 0L, 80.0 ] ]
  let v = evalAt s 10_000L (okExpr "x / 4") |> vecVal
  v.[0].value |> should equal 20.0

[<Fact>]
let ``Linux Host CPU expression evaluates end-to-end`` () =
  // Two CPU modes; idle increases at 0.4/s over the window =>
  // 100 - avg(rate(...)) * 100 = 100 - 0.4 * 100 = 60.
  let s =
    store [
      "node_cpu_seconds_total{mode=\"idle\"}",
        [ 0L, 0.0; 60_000L, 24.0; 120_000L, 48.0; 180_000L, 72.0; 240_000L, 96.0; 300_000L, 120.0 ]
      "node_cpu_seconds_total{mode=\"user\"}",
        [ 0L, 0.0; 60_000L, 0.0; 120_000L, 0.0; 180_000L, 0.0; 240_000L, 0.0; 300_000L, 0.0 ]
    ]
  let expr =
    okExpr """100 - avg(rate(node_cpu_seconds_total{mode="idle"}[5m])) * 100"""
  let v = evalAt s 300_000L expr |> vecVal
  v.Length |> should equal 1
  v.[0].value |> should (equalWithin 1e-6) 60.0

[<Fact>]
let ``vector divided by vector matches on labels`` () =
  let s =
    store [
      "avail{mount=\"/\"}",     [ 0L, 50.0 ]
      "size{mount=\"/\"}",      [ 0L, 100.0 ]
      "avail{mount=\"/data\"}", [ 0L, 25.0 ]
      "size{mount=\"/data\"}",  [ 0L, 100.0 ]
    ]
  let v = evalAt s 10_000L (okExpr "avail / size") |> vecVal
  v.Length |> should equal 2
  v |> valueOf "mount" "/"     |> should equal (Some 0.5)
  v |> valueOf "mount" "/data" |> should equal (Some 0.25)

[<Fact>]
let ``vector / vector skips unmatched series`` () =
  let s =
    store [
      "a{i=\"1\"}", [ 0L, 10.0 ]
      "b{i=\"2\"}", [ 0L, 20.0 ]
    ]
  let v = evalAt s 10_000L (okExpr "a / b") |> vecVal
  v |> should be Empty

// =========================================================================
// Backwards-compat: bare vector selector still works
// =========================================================================

[<Fact>]
let ``bare vector selector still parses via parseVectorSelector`` () =
  match parseVectorSelector """foo{a="b"}""" with
  | Result.Ok sel ->
    sel.name |> should equal (Some "foo")
    sel.matchers.Length |> should equal 1
  | Result.Error e -> failwithf "expected success: %s" e

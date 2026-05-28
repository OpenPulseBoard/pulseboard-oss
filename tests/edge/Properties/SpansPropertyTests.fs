module PulseBoard.Tests.Properties.SpansPropertyTests

open System
open FsCheck
open FsCheck.Xunit
open PulseBoard.Spans

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/// Build a minimal root span (parentSpanId = "").
let private rootSpan traceId spanId service op startMs endMs statusCode : Span =
    { traceId      = traceId
      spanId       = spanId
      parentSpanId = ""
      service      = service
      operation    = op
      kind         = KindServer
      startMs      = startMs
      endMs        = endMs
      statusCode   = statusCode
      attributes   = Map.empty }

/// Build a child span (parentSpanId set).
let private childSpan traceId spanId parentId service op startMs endMs statusCode : Span =
    { traceId      = traceId
      spanId       = spanId
      parentSpanId = parentId
      service      = service
      operation    = op
      kind         = KindClient
      startMs      = startMs
      endMs        = endMs
      statusCode   = statusCode
      attributes   = Map.empty }

let private safeStr (NonEmptyString s : NonEmptyString) =
    s
    |> Seq.filter (fun c -> c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c = '-')
    |> Seq.truncate 20
    |> String.Concat
    |> fun t -> if t.Length = 0 then "svc" else t

// ---------------------------------------------------------------------------
// duration / isError — basic arithmetic invariants
// ---------------------------------------------------------------------------

/// duration is always non-negative (clamped to 0 for inverted timestamps).
[<Property>]
let ``duration is always non-negative``
        (startMs : int64) (endMs : int64) =
    let s = rootSpan "t" "s" "svc" "op" startMs endMs 0
    duration s >= 0L

/// duration returns endMs - startMs when endMs > startMs.
[<Property>]
let ``duration equals endMs minus startMs when endMs > startMs``
        (PositiveInt raw) =
    let startMs = 1000L
    let endMs   = startMs + int64 raw
    let s = rootSpan "t" "s" "svc" "op" startMs endMs 0
    duration s = endMs - startMs

/// isError is true only for statusCode 2.
[<Property>]
let ``isError is true iff statusCode is 2``
        (statusCode : int) =
    let s = rootSpan "t" "s" "svc" "op" 0L 1L statusCode
    isError s = (statusCode = 2)

// ---------------------------------------------------------------------------
// summarise — structural invariants
// ---------------------------------------------------------------------------

/// spanCount always equals the number of input spans.
[<Property>]
let ``summarise spanCount equals input length``
        (rawSpans : (NonEmptyString * NonEmptyString * PositiveInt) list) =
    let spans =
        rawSpans
        |> List.mapi (fun i (svc, op, PositiveInt d) ->
            let t = sprintf "trace%03d" i
            let s = sprintf "%04d" i
            rootSpan t s (safeStr svc) (safeStr op) (int64 i * 100L) (int64 i * 100L + int64 d) 0)
        |> Array.ofList
    if spans.Length = 0 then true
    else (summarise spans).spanCount = spans.Length

/// durationMs is always non-negative.
[<Property>]
let ``summarise durationMs is always non-negative``
        (rawSpans : (NonEmptyString * PositiveInt * PositiveInt) list) =
    let spans =
        rawSpans
        |> List.mapi (fun i (svc, PositiveInt s, PositiveInt d) ->
            let startMs = int64 s
            rootSpan "t" (string i) (safeStr svc) "op" startMs (startMs + int64 d) 0)
        |> Array.ofList
    if spans.Length = 0 then true
    else (summarise spans).durationMs >= 0L

/// errorCount never exceeds spanCount.
[<Property>]
let ``summarise errorCount never exceeds spanCount``
        (rawSpans : (NonEmptyString * int) list) =
    let spans =
        rawSpans
        |> List.mapi (fun i (svc, code) ->
            rootSpan "t" (string i) (safeStr svc) "op" (int64 i * 10L) (int64 i * 10L + 5L) (abs code % 3))
        |> Array.ofList
    if spans.Length = 0 then true
    else
        let s = summarise spans
        s.errorCount <= s.spanCount

/// services contains only distinct entries and is sorted.
[<Property>]
let ``summarise services array is sorted and has no duplicates``
        (rawSpans : (NonEmptyString * PositiveInt) list) =
    let spans =
        rawSpans
        |> List.mapi (fun i (svc, PositiveInt d) ->
            rootSpan "t" (string i) (safeStr svc) "op" (int64 i * 10L) (int64 i * 10L + int64 d) 0)
        |> Array.ofList
    if spans.Length = 0 then true
    else
        let services = (summarise spans).services
        let sorted = services |> Array.sort
        services = sorted &&
        services.Length = (services |> Array.distinct).Length

// ---------------------------------------------------------------------------
// buildMap — structural invariants
// ---------------------------------------------------------------------------

/// Total span count across all nodes equals the number of input spans
/// (every span contributes to exactly one service node).
[<Property>]
let ``buildMap total node spanCount equals input span count``
        (rawSpans : (NonEmptyString * PositiveInt) list) =
    let spans =
        rawSpans
        |> List.mapi (fun i (svc, PositiveInt d) ->
            rootSpan "t" (string i) (safeStr svc) "op" (int64 i * 10L) (int64 i * 10L + int64 d) 0)
        |> Array.ofList
    let m = buildMap spans 0L
    let totalInNodes = m.nodes |> Array.sumBy (fun n -> n.spanCount)
    totalInNodes = spans.Length

/// All node p50, p95, p99 are non-negative.
[<Property>]
let ``buildMap node percentiles are non-negative``
        (rawSpans : (NonEmptyString * PositiveInt) list) =
    let spans =
        rawSpans
        |> List.mapi (fun i (svc, PositiveInt d) ->
            rootSpan "t" (string i) (safeStr svc) "op" (int64 i * 10L) (int64 i * 10L + int64 d) 0)
        |> Array.ofList
    let m = buildMap spans 0L
    m.nodes |> Array.forall (fun n ->
        n.p50Ms >= 0.0 && n.p95Ms >= 0.0 && n.p99Ms >= 0.0)

/// p50 <= p95 <= p99 for every node.
[<Property>]
let ``buildMap node percentiles are ordered p50 <= p95 <= p99``
        (rawSpans : (NonEmptyString * PositiveInt) list) =
    let spans =
        rawSpans
        |> List.mapi (fun i (svc, PositiveInt d) ->
            rootSpan "t" (string i) (safeStr svc) "op" (int64 i * 10L) (int64 i * 10L + int64 d) 0)
        |> Array.ofList
    let m = buildMap spans 0L
    m.nodes |> Array.forall (fun n ->
        n.p50Ms <= n.p95Ms && n.p95Ms <= n.p99Ms)

/// Node errorCount never exceeds spanCount.
[<Property>]
let ``buildMap node errorCount never exceeds spanCount``
        (rawSpans : (NonEmptyString * int) list) =
    let spans =
        rawSpans
        |> List.mapi (fun i (svc, code) ->
            rootSpan "t" (string i) (safeStr svc) "op" (int64 i * 10L) (int64 i * 10L + 5L) (abs code % 3))
        |> Array.ofList
    let m = buildMap spans 0L
    m.nodes |> Array.forall (fun n -> n.errorCount <= n.spanCount)

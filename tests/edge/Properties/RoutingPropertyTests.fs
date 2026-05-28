module PulseBoard.Tests.Properties.RoutingPropertyTests

open System
open System.Text.RegularExpressions
open FsCheck
open FsCheck.Xunit
open PulseBoard.Routing

// ---------------------------------------------------------------------------
// Custom generators
// ---------------------------------------------------------------------------

/// MatchOp values (MEq / MNeq only — avoid regex ops because FsCheck strings
/// may not compile as valid .NET regex patterns).
type AnyEqOp = AnyEqOp of MatchOp

type RoutingGenerators =
    static member AnyEqOp() : Arbitrary<AnyEqOp> =
        Gen.elements [| MEq; MNeq |]
        |> Gen.map AnyEqOp
        |> Arb.fromGen

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

let private safeStr (NonEmptyString s : NonEmptyString) =
    s
    |> Seq.filter (fun c -> c >= ' ' && c <= '~' && c <> '"' && c <> '\\')
    |> Seq.truncate 40
    |> String.Concat
    |> fun t -> if t.Length = 0 then "x" else t

/// Build a Matcher record with explicit re field (compileMatcher is private).
let private mk name (op : MatchOp) value =
    let re =
        match op with
        | MRe | MNRe -> try Some (Regex("^" + value + "$")) with _ -> None
        | _          -> None
    { name = name; op = op; value = value; re = re }

let private defaultRoute receiverId =
    { id              = Guid.NewGuid().ToString "N"
      matchers        = [||]
      receiverId      = Some receiverId
      policyId        = None
      groupBy         = [||]
      groupWaitMs     = 30_000L
      groupIntervalMs = 300_000L
      repeatIntervalMs= 3_600_000L
      continue_       = false
      muteTimeIds     = [||]
      children        = [||] }

let private emptyConfig receiverId : Config =
    { route       = defaultRoute receiverId
      receivers   = [||]
      silences    = [||]
      inhibitions = [||]
      muteTimes   = [||] }

let private roundtrip (c : Config) : Config option =
    match parseConfig (serialiseConfig c) with
    | Result.Ok c2 -> Some c2
    | Result.Error _ -> None

// ---------------------------------------------------------------------------
// matchersMatch — algebraic laws
// ---------------------------------------------------------------------------

/// Empty matcher list matches any label set.
[<Property>]
let ``matchersMatch with empty list is always true``
        (pairs : (NonEmptyString * NonEmptyString) list) =
    let labels =
        pairs
        |> List.truncate 8
        |> List.map (fun (NonEmptyString k, NonEmptyString v) -> safeStr (NonEmptyString k), safeStr (NonEmptyString v))
        |> Map.ofList
    matchersMatch [||] labels = true

/// A single MEq matcher matches iff the label value equals exactly.
[<Property>]
let ``MEq matcher matches only when label value is equal``
        (name : NonEmptyString) (value : NonEmptyString) =
    let n = safeStr name
    let v = safeStr value
    let m = mk n MEq v
    matcherMatches m (Map.ofList [ n, v ]) = true &&
    matcherMatches m (Map.ofList [ n, v + "x" ]) = false

/// A single MNeq matcher matches iff the label value differs.
[<Property>]
let ``MNeq matcher matches when label value differs``
        (name : NonEmptyString) (value : NonEmptyString) =
    let n = safeStr name
    let v = safeStr value
    let m = mk n MNeq v
    matcherMatches m (Map.ofList [ n, v ]) = false &&
    matcherMatches m (Map.ofList [ n, v + "x" ]) = true

/// matchersMatch is the conjunction of individual matcherMatches.
[<Property>]
let ``matchersMatch equals forall matcherMatches``
        (names : NonEmptyString list) =
    let ns =
        names
        |> List.truncate 5
        |> List.map safeStr
        |> List.distinct
    if ns.IsEmpty then true
    else
        let labels = ns |> List.map (fun n -> n, n) |> Map.ofList
        let ms     = ns |> List.map (fun n -> mk n MEq n) |> Array.ofList
        matchersMatch ms labels = (ms |> Array.forall (fun m -> matcherMatches m labels))

// ---------------------------------------------------------------------------
// serialiseConfig / parseConfig — roundtrip invariants
// ---------------------------------------------------------------------------

/// Receiver-id on the root route survives a roundtrip.
[<Property>]
let ``serialiseConfig/parseConfig preserves root route receiverId``
        (receiverId : NonEmptyString) =
    let rid = safeStr receiverId
    match roundtrip (emptyConfig rid) with
    | Some c2 -> c2.route.receiverId = Some rid
    | None    -> false

/// Number of receivers is preserved end-to-end.
[<Property>]
let ``serialiseConfig/parseConfig preserves receiver count``
        (ids : NonEmptyString list) =
    let recvs =
        ids
        |> List.truncate 6
        |> List.map safeStr
        |> List.distinct
        |> List.map (fun rid ->
            { id     = rid
              name   = rid
              type_  = "webhook"
              url    = None
              secret = None
              extra  = Map.empty })
        |> Array.ofList
    let c = { emptyConfig "root" with receivers = recvs }
    match roundtrip c with
    | Some c2 -> c2.receivers.Length = recvs.Length
    | None    -> false

/// groupWaitMs and groupIntervalMs are positive and preserved.
[<Property>]
let ``serialiseConfig/parseConfig preserves positive timing values``
        (PositiveInt gw) (PositiveInt gi) =
    let gwMs = int64 gw
    let giMs = int64 gi
    let r = { defaultRoute "r" with groupWaitMs = gwMs; groupIntervalMs = giMs }
    let c = { emptyConfig "r" with route = r }
    match roundtrip c with
    | Some c2 ->
        c2.route.groupWaitMs     = gwMs &&
        c2.route.groupIntervalMs = giMs
    | None -> false

/// groupBy label list is preserved end-to-end.
[<Property>]
let ``serialiseConfig/parseConfig preserves groupBy labels``
        (labels : NonEmptyString list) =
    let lbs =
        labels
        |> List.truncate 5
        |> List.map safeStr
        |> List.distinct
        |> Array.ofList
    let r = { defaultRoute "r" with groupBy = lbs }
    let c = { emptyConfig "r" with route = r }
    match roundtrip c with
    | Some c2 -> c2.route.groupBy = lbs
    | None    -> false

/// A leaf MEq matcher on the root route is preserved.
[<Property>]
let ``serialiseConfig/parseConfig preserves a single MEq matcher on root route``
        (name : NonEmptyString) (value : NonEmptyString) =
    let n = safeStr name
    let v = safeStr value
    let m = mk n MEq v
    let r = { defaultRoute "r" with matchers = [| m |] }
    let c = { emptyConfig "r" with route = r }
    match roundtrip c with
    | Some c2 ->
        c2.route.matchers.Length = 1 &&
        c2.route.matchers.[0].name  = n &&
        c2.route.matchers.[0].value = v &&
        c2.route.matchers.[0].op    = MEq
    | None -> false

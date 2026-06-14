module PulseBoard.Tests.Unit.CardinalityKillerTests

open System
open Xunit
open FsUnit.Xunit
open PulseBoard.CardinalityKiller
open PulseBoard.Tenancy

let private rule label reason =
  { label = label; reason = reason; createdAt = 0L }

// -- InMemoryCardinalityKillerStore -----------------------------------------

[<Fact>]
let ``Upsert + IsKilled + List round-trip`` () =
    let store : ICardinalityKillerStore = InMemoryCardinalityKillerStore() :> _
    let tid = TenantId "t1"
    let stored = store.Upsert(tid, rule "user_id" "noisy in cardinality view")
    stored.label    |> should equal "user_id"
    stored.reason   |> should equal "noisy in cardinality view"
    stored.createdAt |> should greaterThan 0L
    store.IsKilled(tid, "user_id") |> should equal true
    store.List tid |> Array.length |> should equal 1

[<Fact>]
let ``Upsert is idempotent on the same label`` () =
    let store : ICardinalityKillerStore = InMemoryCardinalityKillerStore() :> _
    let tid = TenantId "t1"
    store.Upsert(tid, rule "user_id" "first")  |> ignore
    store.Upsert(tid, rule "user_id" "second") |> ignore
    let rows = store.List tid
    rows |> Array.length |> should equal 1
    rows.[0].reason |> should equal "second"

[<Fact>]
let ``Delete returns false when nothing was stored`` () =
    let store : ICardinalityKillerStore = InMemoryCardinalityKillerStore() :> _
    store.Delete(TenantId "t1", "absent") |> should equal false

[<Fact>]
let ``Delete removes the rule and IsKilled flips to false`` () =
    let store : ICardinalityKillerStore = InMemoryCardinalityKillerStore() :> _
    let tid = TenantId "t1"
    store.Upsert(tid, rule "user_id" "x") |> ignore
    store.Delete(tid, "user_id") |> should equal true
    store.IsKilled(tid, "user_id") |> should equal false
    store.List tid |> Array.isEmpty |> should equal true

[<Fact>]
let ``rules are per-tenant`` () =
    let store : ICardinalityKillerStore = InMemoryCardinalityKillerStore() :> _
    let a = TenantId "a"
    let b = TenantId "b"
    store.Upsert(a, rule "user_id" "noise") |> ignore
    store.IsKilled(a, "user_id") |> should equal true
    store.IsKilled(b, "user_id") |> should equal false
    store.List a |> Array.length |> should equal 1
    store.List b |> Array.length |> should equal 0

[<Fact>]
let ``Upsert refuses an empty label`` () =
    let store : ICardinalityKillerStore = InMemoryCardinalityKillerStore() :> _
    (fun () -> store.Upsert(TenantId "t1", rule "   " "x") |> ignore)
      |> should throw typeof<ArgumentException>

// -- stripLabels ------------------------------------------------------------

let private storeWith (labels : string seq) =
    let s : ICardinalityKillerStore = InMemoryCardinalityKillerStore() :> _
    for l in labels do s.Upsert(TenantId "t1", rule l "noise") |> ignore
    s

[<Fact>]
let ``stripLabels passes through metrics with no label block`` () =
    let s = storeWith [ "user_id" ]
    stripLabels s (TenantId "t1") "http_requests_total"
    |> should equal "http_requests_total"

[<Fact>]
let ``stripLabels passes through when nothing matches`` () =
    let s = storeWith [ "user_id" ]
    stripLabels s (TenantId "t1") "http_requests_total{route=\"/api\"}"
    |> should equal "http_requests_total{route=\"/api\"}"

[<Fact>]
let ``stripLabels removes a single matching label`` () =
    let s = storeWith [ "user_id" ]
    stripLabels s (TenantId "t1") "http_requests_total{user_id=\"42\",route=\"/api\"}"
    |> should equal "http_requests_total{route=\"/api\"}"

[<Fact>]
let ``stripLabels removes multiple matching labels`` () =
    let s = storeWith [ "user_id"; "session" ]
    stripLabels s (TenantId "t1") "rps{user_id=\"42\",session=\"x\",route=\"/api\"}"
    |> should equal "rps{route=\"/api\"}"

[<Fact>]
let ``stripLabels collapses to bare metric when every label is killed`` () =
    let s = storeWith [ "user_id"; "session" ]
    stripLabels s (TenantId "t1") "rps{user_id=\"42\",session=\"x\"}"
    |> should equal "rps"

[<Fact>]
let ``stripLabels leaves a malformed name alone`` () =
    let s = storeWith [ "user_id" ]
    stripLabels s (TenantId "t1") "broken{user_id=\"42\""
    |> should equal "broken{user_id=\"42\""

// -- renderManagedBlock + applyToOverlay -----------------------------------

[<Fact>]
let ``renderManagedBlock returns empty for no labels`` () =
    renderManagedBlock [||] |> should equal ""

[<Fact>]
let ``renderManagedBlock emits a labeldrop regex with sorted distinct labels`` () =
    let block = renderManagedBlock [| "session"; "user_id"; "session" |]
    block |> should haveSubstring "[[processors.relabel]]"
    block |> should haveSubstring "action = \"labeldrop\""
    block |> should haveSubstring "regex  = \"^(session|user_id)$\""

[<Fact>]
let ``renderManagedBlock regex-escapes special chars`` () =
    let block = renderManagedBlock [| "a.b"; "x+y" |]
    block |> should haveSubstring "a\\.b"
    block |> should haveSubstring "x\\+y"

[<Fact>]
let ``applyToOverlay seeds an empty overlay`` () =
    let result = applyToOverlay "" [| "user_id" |]
    result |> should haveSubstring "[[processors.relabel]]"
    result |> should haveSubstring "regex  = \"^(user_id)$\""

[<Fact>]
let ``applyToOverlay appends to operator-authored content (no markers)`` () =
    let cur = "[[sources.file_logs]]\npath = \"/var/log/app.log\""
    let result = applyToOverlay cur [| "user_id" |]
    result |> should haveSubstring "[[sources.file_logs]]"
    result |> should haveSubstring "[[processors.relabel]]"

[<Fact>]
let ``applyToOverlay replaces an existing managed block in-place`` () =
    let cur = applyToOverlay "" [| "user_id" |]
    let updated = applyToOverlay cur [| "user_id"; "session" |]
    // Old single-label regex is gone; new multi-label regex present.
    updated.Contains "^(user_id)$"           |> should equal false
    updated |> should haveSubstring "^(session|user_id)$"
    // Only one managed block in the result.
    let parts = updated.Split([| "[[processors.relabel]]" |], StringSplitOptions.None)
    parts.Length |> should equal 2

[<Fact>]
let ``applyToOverlay removes the managed region when label set empties`` () =
    let cur = applyToOverlay "[[sources.file_logs]]\npath = \"/x\""
                              [| "user_id" |]
    let after = applyToOverlay cur [||]
    after |> should haveSubstring "[[sources.file_logs]]"
    after.Contains "[[processors.relabel]]" |> should equal false

// -- parseRules / serialiseRules -------------------------------------------

[<Fact>]
let ``parseRules accepts a single-label payload`` () =
    match parseRules """{"label":"user_id","reason":"noise"}""" with
    | Result.Ok rs ->
        rs.Length |> should equal 1
        rs.[0].label  |> should equal "user_id"
        rs.[0].reason |> should equal "noise"
    | Result.Error msg -> failwithf "expected Ok, got %s" msg

[<Fact>]
let ``parseRules accepts a batch payload`` () =
    match parseRules """{"labels":["a","b","c"],"reason":"clean up"}""" with
    | Result.Ok rs ->
        rs |> Array.map (fun r -> r.label) |> should equal [| "a"; "b"; "c" |]
        rs |> Array.forall (fun r -> r.reason = "clean up") |> should equal true
    | Result.Error msg -> failwithf "expected Ok, got %s" msg

[<Fact>]
let ``parseRules rejects an empty labels array`` () =
    match parseRules """{"labels":[],"reason":"x"}""" with
    | Result.Error _ -> ()
    | Result.Ok _ -> failwith "expected Error"

[<Fact>]
let ``serialiseRules emits a JSON array of objects`` () =
    let arr =
      [| { label = "user_id"; reason = "noise"; createdAt = 100L } |]
    let json = serialiseRules arr
    json |> should haveSubstring "\"label\":\"user_id\""
    json |> should haveSubstring "\"reason\":\"noise\""
    json |> should haveSubstring "\"createdAt\":100"

module PulseBoard.Tests.Unit.AgentGroupsTests

open System
open System.Text
open Xunit
open FsUnit.Xunit
open PulseBoard.AgentGroups
open PulseBoard.Tenancy

// -- model + InMemory store -------------------------------------------------

[<Fact>]
let ``empty default group is materialised on first List`` () =
    let store : IAgentGroupStore = InMemoryAgentGroupStore() :> _
    let tid = TenantId "t1"
    let gs = store.List tid
    gs.Length |> should equal 1
    gs.[0].id |> should equal DefaultGroupId
    gs.[0].version |> should equal 1
    gs.[0].overlayToml |> should equal ""

[<Fact>]
let ``TryGet default returns a synthetic group when not yet stored`` () =
    let store : IAgentGroupStore = InMemoryAgentGroupStore() :> _
    let tid = TenantId "t1"
    match store.TryGet(tid, DefaultGroupId) with
    | Some g ->
        g.id |> should equal DefaultGroupId
        g.version |> should equal 1
    | None -> failwith "expected default to materialise"

[<Fact>]
let ``Upsert creates with version 1 and bumps on update`` () =
    let store : IAgentGroupStore = InMemoryAgentGroupStore() :> _
    let tid = TenantId "t1"
    let g0 = { id = "prod"; name = "Production"; overlayToml = "x = 1"; version = 0; updatedAt = 0L }
    let stored1 = store.Upsert(tid, g0)
    stored1.version |> should equal 1
    let stored2 = store.Upsert(tid, { stored1 with overlayToml = "x = 2" })
    stored2.version |> should equal 2
    stored2.overlayToml |> should equal "x = 2"
    // List should now contain default + prod
    let listed = store.List tid
    listed.Length |> should equal 2

[<Fact>]
let ``Delete refuses the default group`` () =
    let store : IAgentGroupStore = InMemoryAgentGroupStore() :> _
    let tid = TenantId "t1"
    store.Delete(tid, DefaultGroupId) |> should equal false

[<Fact>]
let ``Delete removes a non-default group`` () =
    let store : IAgentGroupStore = InMemoryAgentGroupStore() :> _
    let tid = TenantId "t1"
    let _ = store.Upsert(tid, { id = "staging"; name = "Staging"; overlayToml = ""; version = 0; updatedAt = 0L })
    store.Delete(tid, "staging") |> should equal true
    store.TryGet(tid, "staging") |> should equal None

[<Fact>]
let ``tenants are isolated`` () =
    let store : IAgentGroupStore = InMemoryAgentGroupStore() :> _
    let a = TenantId "a"
    let b = TenantId "b"
    store.Upsert(a, { id = "g1"; name = "g1"; overlayToml = "from-a"; version = 0; updatedAt = 0L }) |> ignore
    store.TryGet(b, "g1") |> should equal None
    (store.List a |> Array.length) |> should equal 2  // default + g1
    (store.List b |> Array.length) |> should equal 1  // just default

// -- signing ----------------------------------------------------------------

[<Fact>]
let ``signCanonical is deterministic and depends on every input`` () =
    let key = Encoding.UTF8.GetBytes "0123456789abcdef0123456789abcdef"
    let base_ = signCanonical key "t1" "default" 1 "body"
    base_ |> should equal (signCanonical key "t1" "default" 1 "body")
    base_ |> should not' (equal (signCanonical key "t2" "default" 1 "body"))
    base_ |> should not' (equal (signCanonical key "t1" "prod"    1 "body"))
    base_ |> should not' (equal (signCanonical key "t1" "default" 2 "body"))
    base_ |> should not' (equal (signCanonical key "t1" "default" 1 "BODY"))

[<Fact>]
let ``verifyHex is true for equal and false for different lengths`` () =
    let key = Encoding.UTF8.GetBytes "0123456789abcdef0123456789abcdef"
    let sig_ = signCanonical key "t1" "default" 1 "body"
    verifyHex sig_ sig_ |> should equal true
    verifyHex sig_ (sig_ + "00") |> should equal false
    verifyHex sig_ "" |> should equal false
    verifyHex null sig_ |> should equal false

[<Fact>]
let ``loadOrInitSecret persists and returns same key on second call`` () =
    let dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString "N")
    System.IO.Directory.CreateDirectory dir |> ignore
    let path = System.IO.Path.Combine(dir, "k.key")
    try
        let (k1, b1) = loadOrInitSecret None path
        let (k2, b2) = loadOrInitSecret None path
        b1 |> should equal b2
        k1.Length |> should equal k2.Length
        k1 |> Array.iteri (fun i v -> k2.[i] |> should equal v)
    finally
        try System.IO.Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``loadOrInitSecret accepts an env-provided base64 key`` () =
    let dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString "N")
    System.IO.Directory.CreateDirectory dir |> ignore
    let path = System.IO.Path.Combine(dir, "k.key")
    try
        let raw = Array.init 32 (fun i -> byte i)
        let env = Convert.ToBase64String raw
        let (k, b) = loadOrInitSecret (Some env) path
        b |> should equal env
        k.Length |> should equal 32
        // Did NOT touch the file, since env was used.
        System.IO.File.Exists path |> should equal false
    finally
        try System.IO.Directory.Delete(dir, true) with _ -> ()

// -- JSON codecs ------------------------------------------------------------

[<Fact>]
let ``parseGroup creates a new group with generated id when none provided`` () =
    match parseGroup None """{"name":"Production","overlayToml":"a=1"}""" with
    | Result.Ok g ->
        g.name |> should equal "Production"
        g.overlayToml |> should equal "a=1"
        g.id.Length |> should be (greaterThan 0)
    | Result.Error e -> failwith e

[<Fact>]
let ``parseGroup preserves id and version from existing on edit`` () =
    let existing = { id = "prod"; name = "P"; overlayToml = "old"; version = 7; updatedAt = 0L }
    match parseGroup (Some existing) """{"name":"Prod","overlayToml":"new"}""" with
    | Result.Ok g ->
        g.id |> should equal "prod"
        g.version |> should equal 7
        g.overlayToml |> should equal "new"
    | Result.Error e -> failwith e

[<Fact>]
let ``serialiseGroups round-trips through System.Text.Json`` () =
    let gs = [|
      { id = "default"; name = "Default"; overlayToml = ""; version = 1; updatedAt = 100L }
      { id = "prod";    name = "Prod";    overlayToml = "x"; version = 3; updatedAt = 200L }
    |]
    let json = serialiseGroups gs
    use doc = System.Text.Json.JsonDocument.Parse json
    doc.RootElement.GetArrayLength() |> should equal 2
    doc.RootElement.[0].GetProperty("id").GetString() |> should equal "default"
    doc.RootElement.[1].GetProperty("version").GetInt32() |> should equal 3

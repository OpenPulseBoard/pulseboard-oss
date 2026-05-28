module PulseBoard.Tests.Unit.TenancyTests

open System
open Xunit
open FsUnit.Xunit
open PulseBoard.Tenancy

// -- tryParsePresented -------------------------------------------------------

[<Fact>]
let ``tryParsePresented returns Some for a well-formed key`` () =
    let result = tryParsePresented "pk_abc123.secretpart"
    result |> Option.isSome |> should be True
    result |> Option.map (fun (ApiKeyId id, _) -> id) |> should equal (Some "abc123")
    result |> Option.map (fun (_, secret) -> secret) |> should equal (Some "secretpart")

[<Fact>]
let ``tryParsePresented returns None for wrong prefix`` () =
    tryParsePresented "sk_abc123.secret" |> should equal None
    tryParsePresented "abc123.secret"    |> should equal None

[<Fact>]
let ``tryParsePresented returns None when dot separator is missing`` () =
    tryParsePresented "pk_abc123secret"  |> should equal None

[<Fact>]
let ``tryParsePresented returns None when id part is empty`` () =
    tryParsePresented "pk_.secret"       |> should equal None

[<Fact>]
let ``tryParsePresented returns None when secret part is empty`` () =
    tryParsePresented "pk_abc123."       |> should equal None

// -- hasScope ---------------------------------------------------------------

[<Fact>]
let ``hasScope returns true when all required bits are present`` () =
    hasScope (Scope.Ingest ||| Scope.Query) Scope.Query  |> should be True
    hasScope (Scope.Ingest ||| Scope.Query) Scope.Ingest |> should be True

[<Fact>]
let ``hasScope returns false when required bit is absent`` () =
    hasScope Scope.Query Scope.Admin   |> should be False
    hasScope Scope.None  Scope.Ingest  |> should be False

[<Fact>]
let ``hasScope returns true for Scope.None as requirement`` () =
    hasScope Scope.Query Scope.None    |> should be True

// -- planToText / tryParsePlan roundtrip ------------------------------------

[<Fact>]
let ``planToText and tryParsePlan roundtrip for all plans`` () =
    for plan in [ Free; Pro; Enterprise ] do
        tryParsePlan (planToText plan) |> should equal (Some plan)

[<Fact>]
let ``tryParsePlan is case-insensitive`` () =
    tryParsePlan "FREE"       |> should equal (Some Free)
    tryParsePlan "Pro"        |> should equal (Some Pro)
    tryParsePlan "ENTERPRISE" |> should equal (Some Enterprise)

[<Fact>]
let ``tryParsePlan returns None for unknown string`` () =
    tryParsePlan "premium"    |> should equal None
    tryParsePlan ""           |> should equal None

// -- scopesForRole ----------------------------------------------------------

[<Fact>]
let ``scopesForRole Viewer grants Query only`` () =
    let s = scopesForRole Role.Viewer
    hasScope s Scope.Query  |> should be True
    hasScope s Scope.Ingest |> should be False
    hasScope s Scope.Admin  |> should be False

[<Fact>]
let ``scopesForRole Editor grants Ingest and Query`` () =
    let s = scopesForRole Role.Editor
    hasScope s Scope.Ingest |> should be True
    hasScope s Scope.Query  |> should be True
    hasScope s Scope.Admin  |> should be False

[<Fact>]
let ``scopesForRole Admin grants all scopes`` () =
    let s = scopesForRole Role.Admin
    hasScope s Scope.Ingest |> should be True
    hasScope s Scope.Query  |> should be True
    hasScope s Scope.Admin  |> should be True

[<Fact>]
let ``scopesForRole Billing grants no API scopes`` () =
    let s = scopesForRole Role.Billing
    hasScope s Scope.Ingest |> should be False
    hasScope s Scope.Query  |> should be False
    hasScope s Scope.Admin  |> should be False

// -- InMemoryTenantStore ----------------------------------------------------

[<Fact>]
let ``CreateTenant is idempotent by slug`` () =
    let store = InMemoryTenantStore()
    let t1 = (store :> ITenantStore).CreateTenant "my-org"
    let t2 = (store :> ITenantStore).CreateTenant "my-org"
    t1.id |> should equal t2.id

[<Fact>]
let ``CreateTenant with different slugs creates distinct tenants`` () =
    let store = InMemoryTenantStore()
    let t1 = (store :> ITenantStore).CreateTenant "org-a"
    let t2 = (store :> ITenantStore).CreateTenant "org-b"
    t1.id |> should not' (equal t2.id)

[<Fact>]
let ``TryGetTenant returns None for unknown id`` () =
    let store = InMemoryTenantStore()
    (store :> ITenantStore).TryGetTenant (TenantId "ghost") |> should equal None

[<Fact>]
let ``TryGetTenant returns Some after CreateTenant`` () =
    let store = InMemoryTenantStore()
    let t = (store :> ITenantStore).CreateTenant "acme"
    (store :> ITenantStore).TryGetTenant t.id |> Option.map (fun x -> x.id) |> should equal (Some t.id)

[<Fact>]
let ``UpdateTenantPlan changes the plan for an existing tenant`` () =
    let store = InMemoryTenantStore()
    let t = (store :> ITenantStore).CreateTenant "biz"
    (store :> ITenantStore).UpdateTenantPlan(t.id, Pro) |> ignore
    let updated = (store :> ITenantStore).TryGetTenant t.id
    updated |> Option.map (fun x -> x.plan) |> should equal (Some Pro)

[<Fact>]
let ``ApiKeysFor returns empty list when no keys issued`` () =
    let store = InMemoryTenantStore()
    let t = (store :> ITenantStore).CreateTenant "new"
    (store :> ITenantStore).ApiKeysFor t.id |> should haveLength 0

// -- IssueApiKey + TryGetApiKey + verify ------------------------------------
// NOTE: These tests run Argon2id (~80ms per hash) — intentionally limited.

[<Fact>]
let ``IssueApiKey produces a pk_<id>.<secret> formatted plaintext`` () =
    let store = InMemoryTenantStore()
    let t  = (store :> ITenantStore).CreateTenant "issue-test"
    let ik = (store :> ITenantStore).IssueApiKey(t.id, "CI token", Role.Editor,
                                                  Scope.Ingest ||| Scope.Query)
    ik.plaintext.StartsWith("pk_") |> should be True
    ik.plaintext.Contains(".")     |> should be True

[<Fact>]
let ``verify returns Some TenantCtx for correct plaintext key`` () =
    let store = InMemoryTenantStore()
    let t  = (store :> ITenantStore).CreateTenant "verify-test"
    let ik = (store :> ITenantStore).IssueApiKey(t.id, "test", Role.Admin,
                                                  Scope.Ingest ||| Scope.Query ||| Scope.Admin)
    let ctx = verify store ik.plaintext
    ctx |> Option.isSome |> should be True
    ctx |> Option.map (fun c -> c.tenant.id) |> should equal (Some t.id)

[<Fact>]
let ``verify returns None for a tampered secret`` () =
    let store = InMemoryTenantStore()
    let t = (store :> ITenantStore).CreateTenant "tamper-test"
    let ik = (store :> ITenantStore).IssueApiKey(t.id, "test", Role.Viewer, Scope.Query)
    // Corrupt the secret portion
    let parts = ik.plaintext.Split('.', 2)
    let bad   = parts.[0] + ".wrong-secret"
    verify store bad |> should equal None

[<Fact>]
let ``verify returns None for a completely unknown key`` () =
    let store = InMemoryTenantStore()
    verify store "pk_nosuchid.anysecret" |> should equal None

module PulseBoard.Tests.Integration.PgTenantStoreTests

open System
open Xunit
open FsUnit.Xunit
open PulseBoard.Tenancy
open PulseBoard.Tests.Helpers.TestPostgres

// ---------------------------------------------------------------------------
// Shared Postgres fixture — one container per test class.
// Tags: Category=Postgres so CI can skip these when Docker is unavailable.
// ---------------------------------------------------------------------------

type PgTenantFixture() =
    let mutable _pg : TestPostgresInstance = Unchecked.defaultof<_>

    member _.Pg = _pg

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            task {
                let! inst = startAsync ()
                _pg <- inst
                PulseBoard.PgTenantStore.ensureSchema inst.ConnectionString
            }
            :> System.Threading.Tasks.Task

        member _.DisposeAsync() =
            (_pg :> IAsyncDisposable).DisposeAsync().AsTask()

// ---------------------------------------------------------------------------
// Helper: short unique slug prefix (8 hex chars) for test isolation.
// ---------------------------------------------------------------------------

let private uid () = Guid.NewGuid().ToString("N").[..7]

let private makeStore (cs : string) =
    PulseBoard.PgTenantStore.PgTenantStore(cs) :> ITenantStore

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

[<Trait("Category", "Postgres")>]
type PgTenantStoreTests(fix : PgTenantFixture) =
    interface IClassFixture<PgTenantFixture>

    // -- CreateTenant -------------------------------------------------------

    [<Fact>]
    member _.``CreateTenant persists and is retrievable by id`` () =
        let store = makeStore fix.Pg.ConnectionString
        let slug  = sprintf "ct-%s" (uid ())
        let t     = store.CreateTenant slug
        t.slug          |> should equal slug
        t.plan          |> should equal Free
        store.TryGetTenant t.id
        |> Option.map (fun x -> x.slug)
        |> should equal (Some slug)

    [<Fact>]
    member _.``CreateTenant is idempotent on slug conflict`` () =
        let store = makeStore fix.Pg.ConnectionString
        let slug  = sprintf "idem-%s" (uid ())
        let t1    = store.CreateTenant slug
        let t2    = store.CreateTenant slug
        t1.id |> should equal t2.id

    // -- TryGetTenantBySlug -------------------------------------------------

    [<Fact>]
    member _.``TryGetTenantBySlug returns None for unknown slug`` () =
        let store = makeStore fix.Pg.ConnectionString
        store.TryGetTenantBySlug (sprintf "no-such-%s" (uid ()))
        |> should equal None

    [<Fact>]
    member _.``TryGetTenantBySlug normalises case`` () =
        let store = makeStore fix.Pg.ConnectionString
        let slug  = sprintf "upper-%s" (uid ())
        let t     = store.CreateTenant slug
        store.TryGetTenantBySlug (slug.ToUpperInvariant())
        |> Option.map (fun x -> x.id)
        |> should equal (Some t.id)

    // -- Tenants list -------------------------------------------------------

    [<Fact>]
    member _.``Tenants includes all created tenants`` () =
        let store = makeStore fix.Pg.ConnectionString
        let s1    = sprintf "list1-%s" (uid ())
        let s2    = sprintf "list2-%s" (uid ())
        store.CreateTenant s1 |> ignore
        store.CreateTenant s2 |> ignore
        let slugs = store.Tenants() |> Array.map (fun t -> t.slug) |> Set.ofArray
        slugs |> Set.contains s1 |> should be True
        slugs |> Set.contains s2 |> should be True

    // -- UpdateTenantPlan ---------------------------------------------------

    [<Fact>]
    member _.``UpdateTenantPlan changes plan and persists`` () =
        let store   = makeStore fix.Pg.ConnectionString
        let t       = store.CreateTenant (sprintf "plan-%s" (uid ()))
        let updated = store.UpdateTenantPlan(t.id, Pro)
        updated                 |> should not' (equal None)
        updated.Value.plan      |> should equal Pro
        store.TryGetTenant t.id
        |> Option.map (fun x -> x.plan)
        |> should equal (Some Pro)

    [<Fact>]
    member _.``UpdateTenantPlan returns None for unknown tenant id`` () =
        let store = makeStore fix.Pg.ConnectionString
        store.UpdateTenantPlan(TenantId "no-such-id", Enterprise)
        |> should equal None

    // -- IssueApiKey + verify -----------------------------------------------

    [<Fact>]
    member _.``IssueApiKey round-trips through verify`` () =
        let store  = makeStore fix.Pg.ConnectionString
        let t      = store.CreateTenant (sprintf "key-%s" (uid ()))
        let issued = store.IssueApiKey(t.id, "ci", Admin, Scope.Ingest ||| Scope.Query)
        issued.plaintext.StartsWith "pk_" |> should be True
        let ctx    = verify store issued.plaintext
        ctx                   |> should not' (equal None)
        ctx.Value.tenant.id   |> should equal t.id
        ctx.Value.role        |> should equal Admin
        ctx.Value.scopes      |> should equal (Scope.Ingest ||| Scope.Query)

    // -- ApiKeysFor ---------------------------------------------------------

    [<Fact>]
    member _.``ApiKeysFor returns all keys scoped to the tenant`` () =
        let store = makeStore fix.Pg.ConnectionString
        let t     = store.CreateTenant (sprintf "mkey-%s" (uid ()))
        store.IssueApiKey(t.id, "k1", Viewer, Scope.Query)             |> ignore
        store.IssueApiKey(t.id, "k2", Editor, Scope.Ingest ||| Scope.Query) |> ignore
        let keys  = store.ApiKeysFor t.id
        keys.Length                                   |> should equal 2
        keys |> Array.map (fun k -> k.label) |> Array.sort
        |> should equal [| "k1"; "k2" |]

    // -- UpsertUser + TryGetUser --------------------------------------------

    [<Fact>]
    member _.``UpsertUser creates then updates on second login`` () =
        let store = makeStore fix.Pg.ConnectionString
        let t     = store.CreateTenant (sprintf "sso-%s" (uid ()))
        let sub   = sprintf "sub-%s" (uid ())
        let u1    = store.UpsertUser(t.id, "https://idp.test", sub, Some "a@test.com", Editor)
        u1.email           |> should equal (Some "a@test.com")
        // Re-login with updated email; id must stay the same.
        let u2 = store.UpsertUser(t.id, "https://idp.test", sub, Some "b@test.com", Viewer)
        u2.id              |> should equal u1.id
        u2.email           |> should equal (Some "b@test.com")

    // -- UpdateUserRole -----------------------------------------------------

    [<Fact>]
    member _.``UpdateUserRole changes the persisted role`` () =
        let store   = makeStore fix.Pg.ConnectionString
        let t       = store.CreateTenant (sprintf "role-%s" (uid ()))
        let sub     = sprintf "sub-%s" (uid ())
        let u       = store.UpsertUser(t.id, "https://idp.test", sub, None, Viewer)
        let updated = store.UpdateUserRole(u.id, Admin)
        updated             |> should not' (equal None)
        updated.Value.role  |> should equal Admin

    // -- UsersFor -----------------------------------------------------------

    [<Fact>]
    member _.``UsersFor returns users scoped to the tenant`` () =
        let store = makeStore fix.Pg.ConnectionString
        let t     = store.CreateTenant (sprintf "uf-%s" (uid ()))
        let sub1  = sprintf "sub1-%s" (uid ())
        let sub2  = sprintf "sub2-%s" (uid ())
        store.UpsertUser(t.id, "https://idp.test", sub1, None, Viewer) |> ignore
        store.UpsertUser(t.id, "https://idp.test", sub2, None, Editor) |> ignore
        store.UsersFor t.id |> Array.length |> should equal 2

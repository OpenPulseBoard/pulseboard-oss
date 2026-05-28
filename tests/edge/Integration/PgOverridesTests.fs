module PulseBoard.Tests.Integration.PgOverridesTests

open System
open Xunit
open FsUnit.Xunit
open PulseBoard.Tenancy
open PulseBoard.Quotas
open PulseBoard.Retention
open PulseBoard.Tests.Helpers.TestPostgres

// ---------------------------------------------------------------------------
// Shared Postgres fixture — applies all three schemas that depend on
// pb_tenants and creates a tenant that override tests can attach to.
// ---------------------------------------------------------------------------

type PgOverridesFixture() =
    let mutable _pg  : TestPostgresInstance = Unchecked.defaultof<_>

    member _.Pg = _pg

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            task {
                let! inst = startAsync ()
                _pg <- inst
                let cs = inst.ConnectionString
                PulseBoard.PgTenantStore.ensureSchema      cs
                PulseBoard.PgQuotaOverrides.ensureSchema   cs
                PulseBoard.PgRetentionOverrides.ensureSchema cs
            }
            :> System.Threading.Tasks.Task

        member _.DisposeAsync() =
            (_pg :> IAsyncDisposable).DisposeAsync().AsTask()

// ---------------------------------------------------------------------------
// Helpers shared by both test classes
// ---------------------------------------------------------------------------

let private uid () = Guid.NewGuid().ToString("N").[..7]

/// Create a throwaway tenant so FK constraints on override tables are satisfied.
let private mkTenant (cs : string) : Tenant =
    let store = PulseBoard.PgTenantStore.PgTenantStore(cs) :> ITenantStore
    store.CreateTenant (sprintf "ov-%s" (uid ()))

// ===========================================================================
// PgQuotaOverrides tests
// ===========================================================================

[<Trait("Category", "Postgres")>]
type PgQuotaOverridesTests(fix : PgOverridesFixture) =
    let makeRepo () =
        PulseBoard.PgQuotaOverrides.PgOverrideRepo(fix.Pg.ConnectionString) :> IOverrideRepo

    interface IClassFixture<PgOverridesFixture>

    // -- UpsertRate + LoadAll -----------------------------------------------

    [<Fact>]
    member _.``UpsertRate then LoadAll includes the override`` () =
        let t    = mkTenant fix.Pg.ConnectionString
        let repo = makeRepo ()
        let lim  = { capacity = 999.0; refillPerSec = 50.0 }
        repo.UpsertRate(t.id, Ingest, lim)

        let rows = repo.LoadAll() |> Seq.toArray
        rows
        |> Array.exists (fun (tid, k, l) ->
               tid = t.id && k = Some Ingest
               && l.capacity = 999.0 && l.refillPerSec = 50.0)
        |> should be True

    [<Fact>]
    member _.``ClearRate removes the override from LoadAll`` () =
        let t    = mkTenant fix.Pg.ConnectionString
        let repo = makeRepo ()
        repo.UpsertRate(t.id, Query, { capacity = 200.0; refillPerSec = 10.0 })
        // Verify it was inserted.
        repo.LoadAll() |> Seq.exists (fun (tid, k, _) -> tid = t.id && k = Some Query)
        |> should be True
        // Clear and check it's gone.
        repo.ClearRate(t.id, Query)
        repo.LoadAll() |> Seq.exists (fun (tid, k, _) -> tid = t.id && k = Some Query)
        |> should be False

    // -- UpsertCardinality + LoadAll ----------------------------------------

    [<Fact>]
    member _.``UpsertCardinality then LoadAll includes the cap row`` () =
        let t    = mkTenant fix.Pg.ConnectionString
        let repo = makeRepo ()
        repo.UpsertCardinality(t.id, 5000)

        let rows = repo.LoadAll() |> Seq.toArray
        // Cardinality rows have Kind = None.
        rows
        |> Array.exists (fun (tid, k, l) ->
               tid = t.id && k = None && l.capacity = 5000.0)
        |> should be True

    [<Fact>]
    member _.``ClearCardinality removes the cap row from LoadAll`` () =
        let t    = mkTenant fix.Pg.ConnectionString
        let repo = makeRepo ()
        repo.UpsertCardinality(t.id, 1000)
        repo.LoadAll() |> Seq.exists (fun (tid, k, _) -> tid = t.id && k = None)
        |> should be True
        repo.ClearCardinality(t.id)
        repo.LoadAll() |> Seq.exists (fun (tid, k, _) -> tid = t.id && k = None)
        |> should be False

    [<Fact>]
    member _.``UpsertRate upserts on duplicate key`` () =
        let t    = mkTenant fix.Pg.ConnectionString
        let repo = makeRepo ()
        repo.UpsertRate(t.id, AlertEval, { capacity = 100.0; refillPerSec = 5.0 })
        repo.UpsertRate(t.id, AlertEval, { capacity = 200.0; refillPerSec = 10.0 })

        let rows = repo.LoadAll() |> Seq.toArray
        // Only one row for this (tid, kind); capacity should be the latest value.
        let matching =
            rows |> Array.filter (fun (tid, k, _) -> tid = t.id && k = Some AlertEval)
        matching.Length |> should equal 1
        let (_, _, lim) = matching.[0]
        lim.capacity |> should equal 200.0

// ===========================================================================
// PgRetentionOverrides tests
// ===========================================================================

[<Trait("Category", "Postgres")>]
type PgRetentionOverridesTests(fix : PgOverridesFixture) =
    let makeRepo () =
        PulseBoard.PgRetentionOverrides.PgRetentionRepo(fix.Pg.ConnectionString) :> IRetentionRepo

    interface IClassFixture<PgOverridesFixture>

    // -- Upsert + LoadAll ---------------------------------------------------

    [<Fact>]
    member _.``Upsert then LoadAll includes the policy`` () =
        let t    = mkTenant fix.Pg.ConnectionString
        let repo = makeRepo ()
        let pol  = { metricsMs = Some 86_400_000L
                     logsMs    = Some 604_800_000L
                     tracesMs  = None }
        repo.Upsert(t.id, pol)

        let rows = repo.LoadAll() |> Seq.toArray
        rows
        |> Array.exists (fun (tid, p) ->
               tid = t.id
               && p.metricsMs = Some 86_400_000L
               && p.logsMs    = Some 604_800_000L
               && p.tracesMs  = None)
        |> should be True

    [<Fact>]
    member _.``Upsert with partial policy round-trips correctly`` () =
        let t    = mkTenant fix.Pg.ConnectionString
        let repo = makeRepo ()
        // Only logsMs set; metricsMs and tracesMs are None.
        repo.Upsert(t.id, { metricsMs = None; logsMs = Some 172_800_000L; tracesMs = None })

        let rows = repo.LoadAll() |> Seq.toArray
        rows
        |> Array.exists (fun (tid, p) ->
               tid = t.id
               && p.metricsMs = None
               && p.logsMs    = Some 172_800_000L
               && p.tracesMs  = None)
        |> should be True

    [<Fact>]
    member _.``Clear removes the override from LoadAll`` () =
        let t    = mkTenant fix.Pg.ConnectionString
        let repo = makeRepo ()
        repo.Upsert(t.id, { metricsMs = Some 3_600_000L; logsMs = None; tracesMs = None })
        repo.LoadAll() |> Seq.exists (fun (tid, _) -> tid = t.id) |> should be True
        repo.Clear(t.id)
        repo.LoadAll() |> Seq.exists (fun (tid, _) -> tid = t.id) |> should be False

    [<Fact>]
    member _.``LoadAll returns rows for multiple tenants`` () =
        let t1   = mkTenant fix.Pg.ConnectionString
        let t2   = mkTenant fix.Pg.ConnectionString
        let repo = makeRepo ()
        repo.Upsert(t1.id, { metricsMs = Some 1_000L; logsMs = None; tracesMs = None })
        repo.Upsert(t2.id, { metricsMs = Some 2_000L; logsMs = None; tracesMs = None })

        let rows = repo.LoadAll() |> Seq.toArray
        rows |> Array.exists (fun (tid, _) -> tid = t1.id) |> should be True
        rows |> Array.exists (fun (tid, _) -> tid = t2.id) |> should be True

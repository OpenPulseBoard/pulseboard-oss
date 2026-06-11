module PulseBoard.Tests.Integration.PgRunbookStoreTests

open System
open Xunit
open FsUnit.Xunit
open PulseBoard.Tenancy
open PulseBoard.Runbooks
open PulseBoard.Tests.Helpers.TestPostgres

// ---------------------------------------------------------------------------
// PgRunbookStore integration tests. Exercises the
// Postgres-backed IRunbookStore against a real container: upsert/get
// roundtrip, idempotent overwrite, and per-tenant listing/isolation.
// ---------------------------------------------------------------------------

type PgRunbookFixture() =
    let mutable _pg : TestPostgresInstance = Unchecked.defaultof<_>

    member _.Pg = _pg

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            task {
                let! inst = startAsync ()
                _pg <- inst
                PulseBoard.PgRunbookStore.ensureSchema inst.ConnectionString
            }
            :> System.Threading.Tasks.Task

        member _.DisposeAsync() =
            (_pg :> IAsyncDisposable).DisposeAsync().AsTask()

[<Trait("Category", "Postgres")>]
type PgRunbookStoreTests(fix : PgRunbookFixture) =
    let makeStore () =
        PulseBoard.PgRunbookStore.PgRunbookStore(fix.Pg.ConnectionString) :> IRunbookStore

    let uid () = Guid.NewGuid().ToString("N").[..7]

    let mkProgress fp ruleName (completions : Map<int, StepCompletion>) : RunbookProgress =
        { fingerprint = fp
          ruleId      = "rule-" + ruleName
          ruleName    = ruleName
          runbook     = "## " + ruleName + "\n\n- [ ] step one\n- [ ] step two"
          stepTexts   = [| "step one"; "step two" |]
          firedAt     = 1_000L
          startedAt   = 1_100L
          resolvedAt  = None
          completions = completions }

    interface IClassFixture<PgRunbookFixture>

    // -- upsert + get roundtrip ---------------------------------------------

    [<Fact>]
    member _.``Upsert then Get returns the stored progress`` () =
        let store = makeStore ()
        let tid   = TenantId ("rb-" + uid ())
        let fp    = "fp-" + uid ()
        let p     = mkProgress fp "cpu-high" Map.empty
        store.Upsert(tid, p)

        match store.Get(tid, fp) with
        | None -> failwith "expected a stored record"
        | Some got ->
            got.fingerprint |> should equal fp
            got.ruleName    |> should equal "cpu-high"
            got.stepTexts   |> should equal [| "step one"; "step two" |]
            got.firedAt     |> should equal 1_000L

    [<Fact>]
    member _.``Get on a missing fingerprint returns None`` () =
        let store = makeStore ()
        let tid   = TenantId ("rb-" + uid ())
        store.Get(tid, "nope-" + uid ()) |> should equal (None : RunbookProgress option)

    // -- idempotent overwrite -----------------------------------------------

    [<Fact>]
    member _.``Upsert overwrites by (tenant, fingerprint)`` () =
        let store = makeStore ()
        let tid   = TenantId ("rb-" + uid ())
        let fp    = "fp-" + uid ()
        store.Upsert(tid, mkProgress fp "cpu-high" Map.empty)

        let completed =
            Map.ofList [ 0, { idx = 0; at = 2_000L; user = "alice" } ]
        store.Upsert(tid, { mkProgress fp "cpu-high" completed with resolvedAt = Some 5_000L })

        match store.Get(tid, fp) with
        | None -> failwith "expected a stored record"
        | Some got ->
            got.completions.Count |> should equal 1
            got.completions.[0].user |> should equal "alice"
            got.resolvedAt |> should equal (Some 5_000L)

        // A single logical row — not two.
        store.List tid |> Array.filter (fun r -> r.fingerprint = fp) |> Array.length
        |> should equal 1

    // -- listing + tenant isolation -----------------------------------------

    [<Fact>]
    member _.``List returns only the calling tenant's records, ordered by firedAt`` () =
        let store = makeStore ()
        let t1    = TenantId ("rb-" + uid ())
        let t2    = TenantId ("rb-" + uid ())

        store.Upsert(t1, { mkProgress ("a-" + uid ()) "late"  Map.empty with firedAt = 3_000L })
        store.Upsert(t1, { mkProgress ("b-" + uid ()) "early" Map.empty with firedAt = 1_000L })
        store.Upsert(t2, mkProgress ("c-" + uid ()) "other" Map.empty)

        let rows = store.List t1
        rows.Length |> should equal 2
        rows.[0].ruleName |> should equal "early"
        rows.[1].ruleName |> should equal "late"
        rows |> Array.exists (fun r -> r.ruleName = "other") |> should equal false

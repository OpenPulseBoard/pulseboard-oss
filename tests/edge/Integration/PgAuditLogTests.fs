module PulseBoard.Tests.Integration.PgAuditLogTests

open System
open Xunit
open FsUnit.Xunit
open PulseBoard.Audit
open PulseBoard.Tenancy
open PulseBoard.Tests.Helpers.TestPostgres

// ---------------------------------------------------------------------------
// Shared Postgres fixture — spins up one container per test class.
// ---------------------------------------------------------------------------

type PgAuditFixture() =
    let mutable _pg : TestPostgresInstance = Unchecked.defaultof<_>

    member _.Pg = _pg

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            task {
                let! inst = startAsync ()
                _pg <- inst
                // Audit log has no FK dependency on pb_tenants.
                PulseBoard.PgAuditLog.ensureSchema inst.ConnectionString
            }
            :> System.Threading.Tasks.Task

        member _.DisposeAsync() =
            (_pg :> IAsyncDisposable).DisposeAsync().AsTask()

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

let private uid () = Guid.NewGuid().ToString("N").[..7]

let private makeLog (cs : string) =
    PulseBoard.PgAuditLog.PgAuditLog(cs) :> IAuditLog

let private makeEvent (action : string) (ts : DateTimeOffset) : AuditEvent =
    { ts       = ts
      tenant   = None
      apiKeyId = None
      action   = action
      resource = "/test/resource"
      outcome  = Allow
      remoteIp = None
      details  = None }

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

[<Trait("Category", "Postgres")>]
type PgAuditLogTests(fix : PgAuditFixture) =
    interface IClassFixture<PgAuditFixture>

    // -- ensureSchema -------------------------------------------------------

    [<Fact>]
    member _.``ensureSchema is idempotent when called twice`` () =
        // Should not throw on second call.
        PulseBoard.PgAuditLog.ensureSchema fix.Pg.ConnectionString

    // -- Append + readWindow ------------------------------------------------

    [<Fact>]
    member _.``Append and readWindow round-trip a single event`` () =
        let log    = makeLog fix.Pg.ConnectionString
        let action = sprintf "act-%s" (uid ())
        let ts     = DateTimeOffset.UtcNow
        log.Append (makeEvent action ts)

        let from  = ts.UtcDateTime.AddMilliseconds -1.0
        let until = ts.UtcDateTime.AddSeconds 1.0
        let events =
            PulseBoard.PgAuditLog.readWindow fix.Pg.ConnectionString from until
        events |> Array.exists (fun e -> e.action = action) |> should be True

    [<Fact>]
    member _.``readWindow excludes events before fromTs`` () =
        let log    = makeLog fix.Pg.ConnectionString
        let action = sprintf "before-%s" (uid ())
        let ts     = DateTimeOffset.UtcNow.AddHours -2.0
        log.Append (makeEvent action ts)

        // Window starts 1 hour before now — the event is 2 hours ago, so outside.
        let from  = DateTime.UtcNow.AddHours -1.0
        let until = DateTime.UtcNow.AddMinutes 1.0
        let events =
            PulseBoard.PgAuditLog.readWindow fix.Pg.ConnectionString from until
        events |> Array.exists (fun e -> e.action = action) |> should be False

    [<Fact>]
    member _.``readWindow excludes events at or after untilTs`` () =
        let log    = makeLog fix.Pg.ConnectionString
        let action = sprintf "after-%s" (uid ())
        let ts     = DateTimeOffset.UtcNow.AddHours 2.0
        log.Append (makeEvent action ts)

        // Window ends 1 hour from now — the event is 2 hours ahead, so outside.
        let from  = DateTime.UtcNow.AddMinutes -1.0
        let until = DateTime.UtcNow.AddHours 1.0
        let events =
            PulseBoard.PgAuditLog.readWindow fix.Pg.ConnectionString from until
        events |> Array.exists (fun e -> e.action = action) |> should be False

    [<Fact>]
    member _.``Append with Some tenant and apiKeyId does not throw`` () =
        let log = makeLog fix.Pg.ConnectionString
        let ev  =
            { makeEvent (sprintf "wten-%s" (uid ())) DateTimeOffset.UtcNow with
                tenant   = Some (TenantId "any-tenant")
                apiKeyId = Some (ApiKeyId "any-key")
                details  = Some "extra info" }
        // Should not throw even though tenant FK is not enforced on pb_audit_events.
        log.Append ev

    [<Fact>]
    member _.``IAuditLog Tail always returns empty array`` () =
        // PgAuditLog.Tail is always [] — paged reads use readWindow instead.
        let log = makeLog fix.Pg.ConnectionString
        log.Append (makeEvent (sprintf "tail-%s" (uid ())) DateTimeOffset.UtcNow)
        log.Tail 100 |> should equal [||]

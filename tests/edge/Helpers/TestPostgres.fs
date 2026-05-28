module PulseBoard.Tests.Helpers.TestPostgres

open System
open System.Threading.Tasks
open Testcontainers.PostgreSql

// ---------------------------------------------------------------------------
// TestPostgres — a disposable Postgres container for integration tests.
//
// Uses Testcontainers to spin up a real PostgreSQL 16 instance on a random
// host port. Use for tests that exercise PgTenantStore, PgAuditLog,
// PgQuotaOverrides, and PgRetentionOverrides.
//
// Usage (xUnit with IAsyncLifetime):
//
//   type MyTests() =
//     let mutable pg : TestPostgresInstance = Unchecked.defaultof<_>
//
//     interface IAsyncLifetime with
//       member _.InitializeAsync() = task {
//         let! inst = TestPostgres.startAsync ()
//         pg <- inst
//       }
//       member _.DisposeAsync() = pg.DisposeAsync()
//
//     [<Fact>]
//     member _.``some postgres test``() = task {
//       // pg.ConnectionString is a valid Npgsql connection string
//       PulseBoard.PgTenantStore.ensureSchema pg.ConnectionString
//       ...
//     }
//
// NOTE: Requires Docker (or Podman with the Docker socket) to be running.
// Tests that use this fixture will be marked [<Trait("Category","Postgres")>]
// so they can be excluded with --filter "Category!=Postgres" in environments
// without Docker.
// ---------------------------------------------------------------------------

/// A running Postgres test container. Dispose to stop and remove it.
type TestPostgresInstance (container : PostgreSqlContainer) =
  /// Npgsql connection string pointing at the container.
  member _.ConnectionString = container.GetConnectionString()
  interface IAsyncDisposable with
    member _.DisposeAsync() = container.DisposeAsync()

/// Start a Postgres container and wait until it is ready.
let startAsync () : Task<TestPostgresInstance> = task {
  let container =
    PostgreSqlBuilder()
      .WithImage("postgres:16-alpine")
      .Build()
  do! container.StartAsync()
  return TestPostgresInstance(container)
}

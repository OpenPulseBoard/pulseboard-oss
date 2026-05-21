module PulseBoard.PgQuotaOverrides

open System
open System.Data
open System.Data.Common
open Npgsql
open PulseBoard.Tenancy
open PulseBoard.Quotas

// Postgres backing for per-tenant quota overrides (PLAN.md Phase 1 step 5).
// One row per (tenant_id, kind). The literal string "cardinality" is used
// as the kind for the integer cardinality cap; `capacity` carries the
// count and `refill_rate` is unused (stored as 0).
//
// Schema is owned in-module and applied idempotently via `ensureSchema`
// alongside `PgTenantStore.ensureSchema` at startup.

let private cardinalityKind = "cardinality"

let private schema = """
CREATE TABLE IF NOT EXISTS pb_quota_overrides (
  tenant_id     TEXT             NOT NULL REFERENCES pb_tenants(id) ON DELETE CASCADE,
  kind          TEXT             NOT NULL,
  capacity      DOUBLE PRECISION NOT NULL,
  refill_rate   DOUBLE PRECISION NOT NULL DEFAULT 0,
  PRIMARY KEY (tenant_id, kind)
);
"""

let ensureSchema (connectionString : string) =
  use conn = new NpgsqlConnection(connectionString)
  conn.Open()
  use cmd = new NpgsqlCommand(schema, conn)
  cmd.ExecuteNonQuery() |> ignore

type PgOverrideRepo(connectionString : string) =

  let openConn () =
    let c = new NpgsqlConnection(connectionString)
    c.Open()
    c

  let exec (sql : string) (bind : NpgsqlCommand -> unit) =
    use conn = openConn ()
    use cmd = new NpgsqlCommand(sql, conn)
    bind cmd
    cmd.ExecuteNonQuery() |> ignore

  interface IOverrideRepo with

    member _.LoadAll () =
      // Materialise eagerly: the override set is small (one row per
      // tenant per kind) and callers iterate it once at startup.
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT tenant_id, kind, capacity, refill_rate FROM pb_quota_overrides",
          conn)
      use r = cmd.ExecuteReader()
      let acc = ResizeArray<TenantId * Kind option * Limit>()
      while r.Read() do
        let tid  = TenantId (r.GetString 0)
        let kind = r.GetString 1
        let cap  = r.GetDouble 2
        let rate = r.GetDouble 3
        if kind = cardinalityKind then
          acc.Add(tid, None, { capacity = cap; refillPerSec = 0.0 })
        else
          match tryParseKind kind with
          | Some k -> acc.Add(tid, Some k, { capacity = cap; refillPerSec = rate })
          | None   -> ()  // unknown kind from a newer schema; skip
      acc :> seq<_>

    member _.UpsertRate (TenantId tid, kind, limit) =
      exec
        "INSERT INTO pb_quota_overrides (tenant_id, kind, capacity, refill_rate) \
         VALUES (@tid, @kind, @cap, @rate) \
         ON CONFLICT (tenant_id, kind) DO UPDATE \
           SET capacity = EXCLUDED.capacity, refill_rate = EXCLUDED.refill_rate"
        (fun c ->
          c.Parameters.AddWithValue("tid",  tid)            |> ignore
          c.Parameters.AddWithValue("kind", kindStr kind)   |> ignore
          c.Parameters.AddWithValue("cap",  limit.capacity) |> ignore
          c.Parameters.AddWithValue("rate", limit.refillPerSec) |> ignore)

    member _.ClearRate (TenantId tid, kind) =
      exec
        "DELETE FROM pb_quota_overrides WHERE tenant_id = @tid AND kind = @kind"
        (fun c ->
          c.Parameters.AddWithValue("tid",  tid)          |> ignore
          c.Parameters.AddWithValue("kind", kindStr kind) |> ignore)

    member _.UpsertCardinality (TenantId tid, cap) =
      exec
        "INSERT INTO pb_quota_overrides (tenant_id, kind, capacity, refill_rate) \
         VALUES (@tid, @kind, @cap, 0) \
         ON CONFLICT (tenant_id, kind) DO UPDATE \
           SET capacity = EXCLUDED.capacity, refill_rate = 0"
        (fun c ->
          c.Parameters.AddWithValue("tid",  tid)               |> ignore
          c.Parameters.AddWithValue("kind", cardinalityKind)   |> ignore
          c.Parameters.AddWithValue("cap",  float cap)         |> ignore)

    member _.ClearCardinality (TenantId tid) =
      exec
        "DELETE FROM pb_quota_overrides WHERE tenant_id = @tid AND kind = @kind"
        (fun c ->
          c.Parameters.AddWithValue("tid",  tid)             |> ignore
          c.Parameters.AddWithValue("kind", cardinalityKind) |> ignore)

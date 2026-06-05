module PulseBoard.PgSyntheticStore

// Postgres-backed ISyntheticStore (PLAN-NEXT 14.8).
//
// One row per (tenant_id, check_id) in pb_synthetic_check. The whole `Check`
// record is stored as an opaque JSON body using the same `serialiseCheck` /
// `parseCheck` codecs the file store uses, so the on-disk and in-database
// shapes stay in lock-step.

open System
open Npgsql
open PulseBoard.Tenancy
open PulseBoard.Synthetics

let private schema = """
CREATE TABLE IF NOT EXISTS pb_synthetic_check (
  tenant_id  TEXT   NOT NULL,
  check_id   TEXT   NOT NULL,
  name       TEXT   NOT NULL,
  body       TEXT   NOT NULL,
  PRIMARY KEY (tenant_id, check_id)
);
CREATE INDEX IF NOT EXISTS pb_synthetic_check_tenant_idx
  ON pb_synthetic_check (tenant_id);
"""

let ensureSchema (connectionString : string) =
  use conn = new NpgsqlConnection(connectionString)
  conn.Open()
  use cmd = new NpgsqlCommand(schema, conn)
  cmd.ExecuteNonQuery() |> ignore

let private tid (TenantId t) = t

type PgSyntheticStore(connectionString : string) =

  let openConn () =
    let c = new NpgsqlConnection(connectionString)
    c.Open()
    c

  interface ISyntheticStore with

    member _.List (tenantId : TenantId) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT body FROM pb_synthetic_check \
           WHERE tenant_id = @tid ORDER BY name",
          conn)
      cmd.Parameters.AddWithValue("tid", tid tenantId) |> ignore
      use reader = cmd.ExecuteReader()
      let results = System.Collections.Generic.List<Check>()
      while reader.Read() do
        match parseCheck (reader.GetString 0) with
        | Some c -> results.Add c
        | None   -> ()
      results.ToArray()

    member _.TryGet (tenantId : TenantId, checkId : string) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT body FROM pb_synthetic_check \
           WHERE tenant_id = @tid AND check_id = @cid",
          conn)
      cmd.Parameters.AddWithValue("tid", tid tenantId) |> ignore
      cmd.Parameters.AddWithValue("cid", checkId)       |> ignore
      let result = cmd.ExecuteScalar()
      if isNull result then None
      else parseCheck (result :?> string)

    member _.Upsert (tenantId : TenantId, check : Check) =
      let body = serialiseCheck check
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "INSERT INTO pb_synthetic_check (tenant_id, check_id, name, body) \
           VALUES (@tid, @cid, @name, @body) \
           ON CONFLICT (tenant_id, check_id) DO UPDATE \
             SET name = EXCLUDED.name, body = EXCLUDED.body",
          conn)
      cmd.Parameters.AddWithValue("tid",  tid tenantId) |> ignore
      cmd.Parameters.AddWithValue("cid",  check.id)     |> ignore
      cmd.Parameters.AddWithValue("name", check.name)   |> ignore
      cmd.Parameters.AddWithValue("body", body)         |> ignore
      cmd.ExecuteNonQuery() |> ignore

    member _.Delete (tenantId : TenantId, checkId : string) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "DELETE FROM pb_synthetic_check \
           WHERE tenant_id = @tid AND check_id = @cid",
          conn)
      cmd.Parameters.AddWithValue("tid", tid tenantId) |> ignore
      cmd.Parameters.AddWithValue("cid", checkId)       |> ignore
      cmd.ExecuteNonQuery() > 0

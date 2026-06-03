module PulseBoard.PgDashboardRepo

// Postgres-backed IDashboardRepo so saved dashboards survive deployments.
// Without this the file-backed repo requires a persistent volume mounted
// in every workspace pod; with Postgres the dashboards live in the shared
// database alongside tenants, agents and audit events.
//
// Schema is owned in-module and applied idempotently via `ensureSchema`
// at startup, consistent with every other Pg* module.
//
// Body storage: the full dashboard JSON is kept in a TEXT column and
// round-tripped through the existing `serialiseDashboard` / `parseDashboard`
// functions, so panel schema evolution happens in the SPA and the DB row
// is always opaque to the store layer — identical to the file-backed
// behaviour.

open System
open Npgsql
open PulseBoard.Tenancy
open PulseBoard.Dashboards

let private schema = """
CREATE TABLE IF NOT EXISTS pb_dashboards (
  tenant_id TEXT NOT NULL,
  id        TEXT NOT NULL,
  body      TEXT NOT NULL,
  PRIMARY KEY (tenant_id, id)
);
CREATE INDEX IF NOT EXISTS pb_dashboards_tenant_idx ON pb_dashboards (tenant_id);
"""

let ensureSchema (connectionString : string) =
  use conn = new NpgsqlConnection(connectionString)
  conn.Open()
  use cmd = new NpgsqlCommand(schema, conn)
  cmd.ExecuteNonQuery() |> ignore

let private tid (TenantId t) = t

type PgDashboardRepo(connectionString : string) =

  let openConn () =
    let c = new NpgsqlConnection(connectionString)
    c.Open()
    c

  interface IDashboardRepo with

    member _.List (tenantId : TenantId) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT body FROM pb_dashboards WHERE tenant_id = @tid ORDER BY body->'title'",
          conn)
      cmd.Parameters.AddWithValue("tid", tid tenantId) |> ignore
      use reader = cmd.ExecuteReader()
      let results = System.Collections.Generic.List<Dashboard>()
      while reader.Read() do
        match parseDashboard (reader.GetString 0) with
        | Result.Ok d -> results.Add d
        | Result.Error _ -> ()
      results |> Seq.sortBy (fun d -> d.title) |> Seq.toArray

    member _.TryGet (tenantId : TenantId, id : string) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT body FROM pb_dashboards WHERE tenant_id = @tid AND id = @id",
          conn)
      cmd.Parameters.AddWithValue("tid", tid tenantId) |> ignore
      cmd.Parameters.AddWithValue("id",  id)           |> ignore
      use reader = cmd.ExecuteReader()
      if reader.Read() then
        match parseDashboard (reader.GetString 0) with
        | Result.Ok d -> Some d
        | Result.Error _ -> None
      else
        None

    member _.Upsert (tenantId : TenantId, d : Dashboard) =
      let body = serialiseDashboard d
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "INSERT INTO pb_dashboards (tenant_id, id, body) \
           VALUES (@tid, @id, @body) \
           ON CONFLICT (tenant_id, id) DO UPDATE SET body = EXCLUDED.body",
          conn)
      cmd.Parameters.AddWithValue("tid",  tid tenantId) |> ignore
      cmd.Parameters.AddWithValue("id",   d.id)         |> ignore
      cmd.Parameters.AddWithValue("body", body)         |> ignore
      cmd.ExecuteNonQuery() |> ignore

    member _.Delete (tenantId : TenantId, id : string) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "DELETE FROM pb_dashboards WHERE tenant_id = @tid AND id = @id",
          conn)
      cmd.Parameters.AddWithValue("tid", tid tenantId) |> ignore
      cmd.Parameters.AddWithValue("id",  id)           |> ignore
      cmd.ExecuteNonQuery() = 1

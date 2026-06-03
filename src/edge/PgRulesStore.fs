module PulseBoard.PgRulesStore

// Postgres-backed IRuleStore. Rule groups are stored as a single opaque
// JSON body per (tenant_id, id), consistent with PgDashboardRepo and the
// file-backed FileRuleStore.

open System
open Npgsql
open PulseBoard.Tenancy
open PulseBoard.Rules

let private schema = """
CREATE TABLE IF NOT EXISTS pb_rule_groups (
  tenant_id TEXT NOT NULL,
  id        TEXT NOT NULL,
  body      TEXT NOT NULL,
  PRIMARY KEY (tenant_id, id)
);
CREATE INDEX IF NOT EXISTS pb_rule_groups_tenant_idx ON pb_rule_groups (tenant_id);
"""

let ensureSchema (connectionString : string) =
  use conn = new NpgsqlConnection(connectionString)
  conn.Open()
  use cmd = new NpgsqlCommand(schema, conn)
  cmd.ExecuteNonQuery() |> ignore

let private tid (TenantId t) = t

type PgRuleStore(connectionString : string) =

  let openConn () =
    let c = new NpgsqlConnection(connectionString)
    c.Open()
    c

  interface IRuleStore with

    member _.List (tenantId : TenantId) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT body FROM pb_rule_groups WHERE tenant_id = @tid",
          conn)
      cmd.Parameters.AddWithValue("tid", tid tenantId) |> ignore
      use reader = cmd.ExecuteReader()
      let results = System.Collections.Generic.List<RuleGroup>()
      while reader.Read() do
        match parseGroup (reader.GetString 0) with
        | Result.Ok g -> results.Add g
        | Result.Error _ -> ()
      results |> Seq.sortBy (fun g -> g.name) |> Seq.toArray

    member _.TryGet (tenantId : TenantId, id : string) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT body FROM pb_rule_groups WHERE tenant_id = @tid AND id = @id",
          conn)
      cmd.Parameters.AddWithValue("tid", tid tenantId) |> ignore
      cmd.Parameters.AddWithValue("id",  id)           |> ignore
      use reader = cmd.ExecuteReader()
      if reader.Read() then
        match parseGroup (reader.GetString 0) with
        | Result.Ok g -> Some g
        | Result.Error _ -> None
      else None

    member _.Upsert (tenantId : TenantId, g : RuleGroup) =
      let body = serialiseGroup g
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "INSERT INTO pb_rule_groups (tenant_id, id, body) \
           VALUES (@tid, @id, @body) \
           ON CONFLICT (tenant_id, id) DO UPDATE SET body = EXCLUDED.body",
          conn)
      cmd.Parameters.AddWithValue("tid",  tid tenantId) |> ignore
      cmd.Parameters.AddWithValue("id",   g.id)         |> ignore
      cmd.Parameters.AddWithValue("body", body)         |> ignore
      cmd.ExecuteNonQuery() |> ignore

    member _.Delete (tenantId : TenantId, id : string) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "DELETE FROM pb_rule_groups WHERE tenant_id = @tid AND id = @id",
          conn)
      cmd.Parameters.AddWithValue("tid", tid tenantId) |> ignore
      cmd.Parameters.AddWithValue("id",  id)           |> ignore
      cmd.ExecuteNonQuery() = 1

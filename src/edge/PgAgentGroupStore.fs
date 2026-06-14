module PulseBoard.PgAgentGroupStore

// Postgres-backed IAgentGroupStore. Mirrors the FileAgentGroupStore /
// in-memory contract: rows per (tenant_id, id), the default group is
// auto-materialised on read for tenants that have never explicitly
// stored one.

open System
open Npgsql
open PulseBoard.Tenancy
open PulseBoard.AgentGroups

let private schema = """
CREATE TABLE IF NOT EXISTS pb_agent_groups (
  tenant_id     TEXT   NOT NULL,
  id            TEXT   NOT NULL,
  name          TEXT   NOT NULL DEFAULT '',
  overlay_toml  TEXT   NOT NULL DEFAULT '',
  version       INTEGER NOT NULL DEFAULT 1,
  updated_at_ms BIGINT NOT NULL,
  PRIMARY KEY (tenant_id, id)
);
"""

let ensureSchema (connectionString : string) =
  use conn = new NpgsqlConnection(connectionString)
  conn.Open()
  use cmd = new NpgsqlCommand(schema, conn)
  cmd.ExecuteNonQuery() |> ignore

let private nowMs () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

type PgAgentGroupStore(connectionString : string) =

  let openConn () =
    let c = new NpgsqlConnection(connectionString)
    c.Open()
    c

  let readRow (r : System.Data.Common.DbDataReader) : AgentGroup =
    { id          = r.GetString 1
      name        = r.GetString 2
      overlayToml = r.GetString 3
      version     = r.GetInt32  4
      updatedAt   = r.GetInt64  5 }

  interface IAgentGroupStore with

    member _.List tenant =
      let (TenantId tid) = tenant
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT tenant_id, id, name, overlay_toml, version, updated_at_ms \
             FROM pb_agent_groups WHERE tenant_id = @tid ORDER BY id",
          conn)
      cmd.Parameters.AddWithValue("tid", tid) |> ignore
      use r = cmd.ExecuteReader()
      let acc = ResizeArray<AgentGroup>()
      while r.Read() do acc.Add(readRow r)
      let arr = acc.ToArray()
      if arr |> Array.exists (fun g -> g.id = DefaultGroupId) then arr
      else Array.append [| emptyDefaultGroup () |] arr

    member _.TryGet (tenant, id) =
      let (TenantId tid) = tenant
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT tenant_id, id, name, overlay_toml, version, updated_at_ms \
             FROM pb_agent_groups WHERE tenant_id = @tid AND id = @id",
          conn)
      cmd.Parameters.AddWithValue("tid", tid) |> ignore
      cmd.Parameters.AddWithValue("id",  id)  |> ignore
      use r = cmd.ExecuteReader()
      if r.Read() then Some (readRow r)
      elif id = DefaultGroupId then Some (emptyDefaultGroup ())
      else None

    member _.Upsert (tenant, group) =
      let (TenantId tid) = tenant
      let id = if String.IsNullOrWhiteSpace group.id then DefaultGroupId else group.id
      let now = nowMs()
      use conn = openConn ()
      // INSERT with ON CONFLICT bumps version using the existing row's
      // value, mirroring InMemory semantics (creation = 1, then +1 each
      // edit).
      use cmd =
        new NpgsqlCommand(
          "INSERT INTO pb_agent_groups (tenant_id, id, name, overlay_toml, version, updated_at_ms) \
           VALUES (@tid, @id, @name, @overlay, 1, @upd) \
           ON CONFLICT (tenant_id, id) DO UPDATE \
             SET name = EXCLUDED.name, \
                 overlay_toml = EXCLUDED.overlay_toml, \
                 version = pb_agent_groups.version + 1, \
                 updated_at_ms = EXCLUDED.updated_at_ms \
           RETURNING version, updated_at_ms",
          conn)
      cmd.Parameters.AddWithValue("tid",     tid)               |> ignore
      cmd.Parameters.AddWithValue("id",      id)                |> ignore
      cmd.Parameters.AddWithValue("name",    group.name)        |> ignore
      cmd.Parameters.AddWithValue("overlay", group.overlayToml) |> ignore
      cmd.Parameters.AddWithValue("upd",     now)               |> ignore
      use r = cmd.ExecuteReader()
      if r.Read() then
        { group with
            id        = id
            version   = r.GetInt32 0
            updatedAt = r.GetInt64 1 }
      else
        { group with id = id; version = 1; updatedAt = now }

    member _.Delete (tenant, id) =
      if id = DefaultGroupId then false
      else
        let (TenantId tid) = tenant
        use conn = openConn ()
        use cmd =
          new NpgsqlCommand(
            "DELETE FROM pb_agent_groups WHERE tenant_id = @tid AND id = @id",
            conn)
        cmd.Parameters.AddWithValue("tid", tid) |> ignore
        cmd.Parameters.AddWithValue("id",  id)  |> ignore
        cmd.ExecuteNonQuery() > 0

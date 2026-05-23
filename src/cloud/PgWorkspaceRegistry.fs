module PulseBoard.PgWorkspaceRegistry

open System
open Npgsql
open PulseBoard.Provisioner

// Postgres backing for the provisioner's slug → workspace mapping
// (PLAN.md Phase 9, "Postgres-backed IWorkspaceRegistry"). One row per
// slug. Survives provisioner restarts so a deployed Caddy can still
// resolve `/provision/route` for previously-issued subdomains.
//
// Schema is owned in-module and applied idempotently via `ensureSchema`
// at startup, matching the pattern used by PgTenantStore /
// PgQuotaOverrides / PgAuditLog.

let private schema = """
CREATE TABLE IF NOT EXISTS pb_workspaces (
  slug          TEXT        PRIMARY KEY,
  fly_app_name  TEXT        NOT NULL,
  upstream_url  TEXT        NOT NULL,
  tenant_id     TEXT,
  api_key_id    TEXT,
  owner_email   TEXT        NOT NULL,
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  archived_at   TIMESTAMPTZ
);
-- Idempotent migration for clusters that pre-date the archived_at column.
ALTER TABLE pb_workspaces ADD COLUMN IF NOT EXISTS archived_at TIMESTAMPTZ;

CREATE TABLE IF NOT EXISTS pb_workspace_heartbeats (
  slug          TEXT        PRIMARY KEY REFERENCES pb_workspaces(slug) ON DELETE CASCADE,
  last_seen_at  TIMESTAMPTZ NOT NULL,
  version       TEXT
);
"""

let ensureSchema (connectionString : string) =
  use conn = new NpgsqlConnection(connectionString)
  conn.Open()
  use cmd = new NpgsqlCommand(schema, conn)
  cmd.ExecuteNonQuery() |> ignore

let private readRecord (r : System.Data.Common.DbDataReader) : WorkspaceRecord =
  let optStr (i : int) =
    if r.IsDBNull i then None else Some (r.GetString i)
  let optDate (i : int) =
    if r.IsDBNull i then None
    else Some (DateTimeOffset(r.GetDateTime i, TimeSpan.Zero))
  { slug        = r.GetString 0
    flyAppName  = r.GetString 1
    upstreamUrl = r.GetString 2
    tenantId    = optStr 3
    apiKeyId    = optStr 4
    ownerEmail  = r.GetString 5
    createdAt   = DateTimeOffset(r.GetDateTime 6, TimeSpan.Zero)
    archivedAt  = optDate 7 }

type PgWorkspaceRegistry(connectionString : string) =

  let openConn () =
    let c = new NpgsqlConnection(connectionString)
    c.Open()
    c

  let trySelectOne (sql : string) (bind : NpgsqlCommand -> unit) : WorkspaceRecord option =
    use conn = openConn ()
    use cmd = new NpgsqlCommand(sql, conn)
    bind cmd
    use r = cmd.ExecuteReader()
    if r.Read() then Some (readRecord r) else None

  let selectCols =
    "slug, fly_app_name, upstream_url, tenant_id, api_key_id, owner_email, created_at, archived_at"

  interface IWorkspaceRegistry with

    member _.Insert (rec_ : WorkspaceRecord) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "INSERT INTO pb_workspaces \
             (slug, fly_app_name, upstream_url, tenant_id, api_key_id, owner_email, created_at) \
           VALUES (@slug, @app, @url, @tid, @kid, @email, @ts) \
           ON CONFLICT (slug) DO UPDATE SET \
             fly_app_name = EXCLUDED.fly_app_name, \
             upstream_url = EXCLUDED.upstream_url, \
             tenant_id    = EXCLUDED.tenant_id, \
             api_key_id   = EXCLUDED.api_key_id, \
             owner_email  = EXCLUDED.owner_email",
          conn)
      cmd.Parameters.AddWithValue("slug",  rec_.slug)        |> ignore
      cmd.Parameters.AddWithValue("app",   rec_.flyAppName)  |> ignore
      cmd.Parameters.AddWithValue("url",   rec_.upstreamUrl) |> ignore
      cmd.Parameters.AddWithValue("tid",
        match rec_.tenantId with Some s -> box s | None -> box DBNull.Value) |> ignore
      cmd.Parameters.AddWithValue("kid",
        match rec_.apiKeyId with Some s -> box s | None -> box DBNull.Value) |> ignore
      cmd.Parameters.AddWithValue("email", rec_.ownerEmail)  |> ignore
      cmd.Parameters.AddWithValue("ts",    rec_.createdAt.UtcDateTime) |> ignore
      cmd.ExecuteNonQuery() |> ignore

    member _.TryGetBySlug (slug : string) =
      trySelectOne
        (sprintf "SELECT %s FROM pb_workspaces WHERE slug = @slug" selectCols)
        (fun c -> c.Parameters.AddWithValue("slug", slug.ToLowerInvariant()) |> ignore)

    member this.TryGetByHost (host : string) =
      // Same convention as InMemoryWorkspaceRegistry: strip everything
      // from the first '.' onward so "<slug>.pulseboard.cloud" → "<slug>".
      let s =
        let idx = host.IndexOf '.'
        if idx > 0 then host.Substring(0, idx).ToLowerInvariant()
        else host.ToLowerInvariant()
      (this :> IWorkspaceRegistry).TryGetBySlug s

    member this.Update (slug : string) (f : WorkspaceRecord -> WorkspaceRecord) =
      // Read-modify-write inside one transaction so concurrent updates
      // don't lose fields. SELECT ... FOR UPDATE locks the row.
      use conn = openConn ()
      use tx = conn.BeginTransaction()
      let current =
        use cmd =
          new NpgsqlCommand(
            sprintf "SELECT %s FROM pb_workspaces WHERE slug = @slug FOR UPDATE" selectCols,
            conn, tx)
        cmd.Parameters.AddWithValue("slug", slug) |> ignore
        use r = cmd.ExecuteReader()
        if r.Read() then Some (readRecord r) else None
      match current with
      | None ->
        tx.Rollback()
        failwithf "no such slug: %s" slug
      | Some old ->
        let updated = f old
        use cmd =
          new NpgsqlCommand(
            "UPDATE pb_workspaces SET \
               fly_app_name = @app, \
               upstream_url = @url, \
               tenant_id    = @tid, \
               api_key_id   = @kid, \
               owner_email  = @email \
             WHERE slug = @slug",
            conn, tx)
        cmd.Parameters.AddWithValue("slug",  slug)               |> ignore
        cmd.Parameters.AddWithValue("app",   updated.flyAppName) |> ignore
        cmd.Parameters.AddWithValue("url",   updated.upstreamUrl)|> ignore
        cmd.Parameters.AddWithValue("tid",
          match updated.tenantId with Some s -> box s | None -> box DBNull.Value) |> ignore
        cmd.Parameters.AddWithValue("kid",
          match updated.apiKeyId with Some s -> box s | None -> box DBNull.Value) |> ignore
        cmd.Parameters.AddWithValue("email", updated.ownerEmail) |> ignore
        cmd.ExecuteNonQuery() |> ignore
        tx.Commit()

    member _.List () =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          sprintf "SELECT %s FROM pb_workspaces ORDER BY created_at DESC" selectCols,
          conn)
      use r = cmd.ExecuteReader()
      let acc = System.Collections.Generic.List<WorkspaceRecord>()
      while r.Read() do acc.Add (readRecord r)
      acc |> List.ofSeq

    member _.SetArchived (slug : string) (at : DateTimeOffset option) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "UPDATE pb_workspaces SET archived_at = @at WHERE slug = @slug",
          conn)
      cmd.Parameters.AddWithValue("slug", slug) |> ignore
      cmd.Parameters.AddWithValue("at",
        match at with
        | Some t -> box t.UtcDateTime
        | None   -> box DBNull.Value) |> ignore
      cmd.ExecuteNonQuery() |> ignore

    member _.Delete (slug : string) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "DELETE FROM pb_workspaces WHERE slug = @slug",
          conn)
      cmd.Parameters.AddWithValue("slug", slug) |> ignore
      cmd.ExecuteNonQuery() |> ignore

/// Postgres-backed heartbeat store. One row per slug; UPSERT on each
/// `Record` so the table never grows beyond the registry's row count.
/// FK with `ON DELETE CASCADE` keeps heartbeats in lockstep with
/// workspace purges — dropping the workspace row drops its heartbeat
/// too.
type PgHeartbeatStore(connectionString : string) =

  let openConn () =
    let c = new NpgsqlConnection(connectionString)
    c.Open()
    c

  interface IHeartbeatStore with

    member _.Record (slug : string) (version : string option) (at : DateTimeOffset) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          """
          INSERT INTO pb_workspace_heartbeats (slug, last_seen_at, version)
          VALUES (@slug, @at, @ver)
          ON CONFLICT (slug) DO UPDATE
            SET last_seen_at = EXCLUDED.last_seen_at,
                version      = EXCLUDED.version
          """, conn)
      cmd.Parameters.AddWithValue("slug", slug) |> ignore
      cmd.Parameters.AddWithValue("at", at.UtcDateTime) |> ignore
      cmd.Parameters.AddWithValue("ver",
        match version with Some v -> box v | None -> box DBNull.Value) |> ignore
      cmd.ExecuteNonQuery() |> ignore

    member _.TryGet (slug : string) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT slug, last_seen_at, version FROM pb_workspace_heartbeats WHERE slug = @slug",
          conn)
      cmd.Parameters.AddWithValue("slug", slug) |> ignore
      use r = cmd.ExecuteReader()
      if r.Read() then
        let version = if r.IsDBNull 2 then None else Some (r.GetString 2)
        Some { slug = r.GetString 0
               lastSeenAt = DateTimeOffset(r.GetDateTime 1, TimeSpan.Zero)
               version = version }
      else None

    member _.All () =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT slug, last_seen_at, version FROM pb_workspace_heartbeats",
          conn)
      use r = cmd.ExecuteReader()
      let acc = System.Collections.Generic.Dictionary<string, Heartbeat>()
      while r.Read() do
        let version = if r.IsDBNull 2 then None else Some (r.GetString 2)
        let h = { slug = r.GetString 0
                  lastSeenAt = DateTimeOffset(r.GetDateTime 1, TimeSpan.Zero)
                  version = version }
        acc.[h.slug] <- h
      acc
      |> Seq.map (fun kv -> kv.Key, kv.Value)
      |> Map.ofSeq

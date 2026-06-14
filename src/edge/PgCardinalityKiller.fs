module PulseBoard.PgCardinalityKiller

// Postgres-backed ICardinalityKillerStore. Schema: one row per
// (tenant_id, label). The hot-path `IsKilled` predicate caches a
// per-tenant HashSet refreshed lazily after every Upsert / Delete so we
// don't open a connection per ingested sample.

open System
open System.Collections.Concurrent
open System.Collections.Generic
open Npgsql
open PulseBoard.Tenancy
open PulseBoard.CardinalityKiller

let private schema = """
CREATE TABLE IF NOT EXISTS pb_cardinality_drops (
  tenant_id  TEXT   NOT NULL,
  label      TEXT   NOT NULL,
  reason     TEXT   NOT NULL DEFAULT '',
  created_at BIGINT NOT NULL,
  PRIMARY KEY (tenant_id, label)
);
"""

let ensureSchema (connectionString : string) =
  use conn = new NpgsqlConnection(connectionString)
  conn.Open()
  use cmd = new NpgsqlCommand(schema, conn)
  cmd.ExecuteNonQuery() |> ignore

let private nowMs () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

type PgCardinalityKillerStore(connectionString : string) =

  // Per-tenant snapshot of active labels. Refreshed on writes; reads
  // populate lazily on first IsKilled call for a tenant.
  let cache = ConcurrentDictionary<string, HashSet<string>>()

  let openConn () =
    let c = new NpgsqlConnection(connectionString)
    c.Open()
    c

  let loadLabels (tid : string) : HashSet<string> =
    use conn = openConn ()
    use cmd =
      new NpgsqlCommand(
        "SELECT label FROM pb_cardinality_drops WHERE tenant_id = @tid",
        conn)
    cmd.Parameters.AddWithValue("tid", tid) |> ignore
    use r = cmd.ExecuteReader()
    let set = HashSet<string>(StringComparer.Ordinal)
    while r.Read() do set.Add(r.GetString 0) |> ignore
    set

  let invalidate (TenantId t) = cache.TryRemove t |> ignore

  interface ICardinalityKillerStore with

    member _.List tenant =
      let (TenantId tid) = tenant
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT label, reason, created_at FROM pb_cardinality_drops \
             WHERE tenant_id = @tid ORDER BY created_at",
          conn)
      cmd.Parameters.AddWithValue("tid", tid) |> ignore
      use r = cmd.ExecuteReader()
      let acc = ResizeArray<DroppedLabel>()
      while r.Read() do
        acc.Add
          { label     = r.GetString 0
            reason    = r.GetString 1
            createdAt = r.GetInt64 2 }
      acc.ToArray()

    member _.Upsert (tenant, rule) =
      if String.IsNullOrWhiteSpace rule.label then
        invalidArg "rule.label" "label must not be empty"
      let (TenantId tid) = tenant
      let label = rule.label.Trim()
      let createdAt = if rule.createdAt > 0L then rule.createdAt else nowMs()
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "INSERT INTO pb_cardinality_drops (tenant_id, label, reason, created_at) \
           VALUES (@tid, @label, @reason, @ca) \
           ON CONFLICT (tenant_id, label) DO UPDATE \
             SET reason = EXCLUDED.reason \
           RETURNING created_at",
          conn)
      cmd.Parameters.AddWithValue("tid",    tid)        |> ignore
      cmd.Parameters.AddWithValue("label",  label)      |> ignore
      cmd.Parameters.AddWithValue("reason", rule.reason)|> ignore
      cmd.Parameters.AddWithValue("ca",     createdAt)  |> ignore
      let stored =
        match cmd.ExecuteScalar() with
        | :? int64 as v -> { label = label; reason = rule.reason; createdAt = v }
        | _             -> { label = label; reason = rule.reason; createdAt = createdAt }
      invalidate tenant
      stored

    member _.Delete (tenant, label) =
      if String.IsNullOrWhiteSpace label then false
      else
        let (TenantId tid) = tenant
        use conn = openConn ()
        use cmd =
          new NpgsqlCommand(
            "DELETE FROM pb_cardinality_drops WHERE tenant_id = @tid AND label = @label",
            conn)
        cmd.Parameters.AddWithValue("tid",   tid)          |> ignore
        cmd.Parameters.AddWithValue("label", label.Trim()) |> ignore
        let n = cmd.ExecuteNonQuery()
        invalidate tenant
        n > 0

    member _.IsKilled (tenant, label) =
      if String.IsNullOrWhiteSpace label then false
      else
        let (TenantId tid) = tenant
        let set = cache.GetOrAdd(tid, fun _ -> loadLabels tid)
        set.Contains label

module PulseBoard.PgOnCallStore

// Postgres-backed ICatalogStore and IAckStore.
//
// Catalog: one opaque JSON body per tenant in pb_oncall_catalogs.
// Acks: one row per (tenant_id, fingerprint) in pb_oncall_acks.

open System
open Npgsql
open PulseBoard.Tenancy
open PulseBoard.OnCall

let private schema = """
CREATE TABLE IF NOT EXISTS pb_oncall_catalogs (
  tenant_id TEXT PRIMARY KEY,
  body      TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS pb_oncall_acks (
  tenant_id   TEXT   NOT NULL,
  fingerprint TEXT   NOT NULL,
  username    TEXT   NOT NULL,
  acked_at_ms BIGINT NOT NULL,
  PRIMARY KEY (tenant_id, fingerprint)
);
CREATE INDEX IF NOT EXISTS pb_oncall_acks_tenant_idx ON pb_oncall_acks (tenant_id);
"""

let ensureSchema (connectionString : string) =
  use conn = new NpgsqlConnection(connectionString)
  conn.Open()
  use cmd = new NpgsqlCommand(schema, conn)
  cmd.ExecuteNonQuery() |> ignore

let private tid (TenantId t) = t

type PgCatalogStore(connectionString : string) =

  let openConn () =
    let c = new NpgsqlConnection(connectionString)
    c.Open()
    c

  interface ICatalogStore with

    member _.Get (tenantId : TenantId) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT body FROM pb_oncall_catalogs WHERE tenant_id = @tid",
          conn)
      cmd.Parameters.AddWithValue("tid", tid tenantId) |> ignore
      let result = cmd.ExecuteScalar()
      if isNull result then emptyCatalog
      else
        match parseCatalog (result :?> string) with
        | Result.Ok c -> c
        | Result.Error _ -> emptyCatalog

    member _.Set (tenantId : TenantId, catalog : Catalog) =
      let body = serialiseCatalog catalog
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "INSERT INTO pb_oncall_catalogs (tenant_id, body) VALUES (@tid, @body) \
           ON CONFLICT (tenant_id) DO UPDATE SET body = EXCLUDED.body",
          conn)
      cmd.Parameters.AddWithValue("tid",  tid tenantId) |> ignore
      cmd.Parameters.AddWithValue("body", body)         |> ignore
      cmd.ExecuteNonQuery() |> ignore

type PgAckStore(connectionString : string) =

  let openConn () =
    let c = new NpgsqlConnection(connectionString)
    c.Open()
    c

  interface IAckStore with

    member _.Ack (tenantId : TenantId, ack : Acknowledgement) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "INSERT INTO pb_oncall_acks (tenant_id, fingerprint, username, acked_at_ms) \
           VALUES (@tid, @fp, @usr, @at) \
           ON CONFLICT (tenant_id, fingerprint) DO UPDATE \
             SET username = EXCLUDED.username, acked_at_ms = EXCLUDED.acked_at_ms",
          conn)
      cmd.Parameters.AddWithValue("tid", tid tenantId)  |> ignore
      cmd.Parameters.AddWithValue("fp",  ack.fingerprint) |> ignore
      cmd.Parameters.AddWithValue("usr", ack.user)        |> ignore
      cmd.Parameters.AddWithValue("at",  ack.ackedAt)     |> ignore
      cmd.ExecuteNonQuery() |> ignore

    member _.IsAcked (tenantId : TenantId, fingerprint : string) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT 1 FROM pb_oncall_acks WHERE tenant_id = @tid AND fingerprint = @fp",
          conn)
      cmd.Parameters.AddWithValue("tid", tid tenantId) |> ignore
      cmd.Parameters.AddWithValue("fp",  fingerprint)  |> ignore
      not (isNull (cmd.ExecuteScalar()))

    member _.List (tenantId : TenantId, fingerprint : string) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT fingerprint, username, acked_at_ms \
           FROM pb_oncall_acks WHERE tenant_id = @tid AND fingerprint = @fp",
          conn)
      cmd.Parameters.AddWithValue("tid", tid tenantId) |> ignore
      cmd.Parameters.AddWithValue("fp",  fingerprint)  |> ignore
      use reader = cmd.ExecuteReader()
      let results = System.Collections.Generic.List<Acknowledgement>()
      while reader.Read() do
        results.Add {
          fingerprint = reader.GetString 0
          user        = reader.GetString 1
          ackedAt     = reader.GetInt64  2 }
      results.ToArray()

    member _.All (tenantId : TenantId) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT fingerprint, username, acked_at_ms \
           FROM pb_oncall_acks WHERE tenant_id = @tid",
          conn)
      cmd.Parameters.AddWithValue("tid", tid tenantId) |> ignore
      use reader = cmd.ExecuteReader()
      let results = System.Collections.Generic.List<Acknowledgement>()
      while reader.Read() do
        results.Add {
          fingerprint = reader.GetString 0
          user        = reader.GetString 1
          ackedAt     = reader.GetInt64  2 }
      results.ToArray()

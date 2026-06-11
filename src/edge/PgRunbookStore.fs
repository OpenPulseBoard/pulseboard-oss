module PulseBoard.PgRunbookStore

// Postgres-backed IRunbookStore.
//
// One row per (tenant_id, fingerprint) in pb_runbook_progress. The whole
// `RunbookProgress` record is stored as an opaque JSON body using the same
// `serialiseProgress` / `parseProgress` codecs the file store uses, so the
// on-disk and in-database shapes stay in lock-step. `firedAt` is mirrored
// into its own column so the post-incident view can order/scope cheaply.

open System
open Npgsql
open PulseBoard.Tenancy
open PulseBoard.Runbooks

let private schema = """
CREATE TABLE IF NOT EXISTS pb_runbook_progress (
  tenant_id   TEXT   NOT NULL,
  fingerprint TEXT   NOT NULL,
  fired_at_ms BIGINT NOT NULL,
  body        TEXT   NOT NULL,
  PRIMARY KEY (tenant_id, fingerprint)
);
CREATE INDEX IF NOT EXISTS pb_runbook_progress_tenant_idx
  ON pb_runbook_progress (tenant_id);
"""

let ensureSchema (connectionString : string) =
  use conn = new NpgsqlConnection(connectionString)
  conn.Open()
  use cmd = new NpgsqlCommand(schema, conn)
  cmd.ExecuteNonQuery() |> ignore

let private tid (TenantId t) = t

type PgRunbookStore(connectionString : string) =

  let openConn () =
    let c = new NpgsqlConnection(connectionString)
    c.Open()
    c

  interface IRunbookStore with

    member _.Get (tenantId : TenantId, fingerprint : string) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT body FROM pb_runbook_progress \
           WHERE tenant_id = @tid AND fingerprint = @fp",
          conn)
      cmd.Parameters.AddWithValue("tid", tid tenantId) |> ignore
      cmd.Parameters.AddWithValue("fp",  fingerprint)  |> ignore
      let result = cmd.ExecuteScalar()
      if isNull result then None
      else parseProgress (result :?> string)

    member _.Upsert (tenantId : TenantId, progress : RunbookProgress) =
      let body = serialiseProgress progress
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "INSERT INTO pb_runbook_progress (tenant_id, fingerprint, fired_at_ms, body) \
           VALUES (@tid, @fp, @fired, @body) \
           ON CONFLICT (tenant_id, fingerprint) DO UPDATE \
             SET fired_at_ms = EXCLUDED.fired_at_ms, body = EXCLUDED.body",
          conn)
      cmd.Parameters.AddWithValue("tid",   tid tenantId)        |> ignore
      cmd.Parameters.AddWithValue("fp",    progress.fingerprint) |> ignore
      cmd.Parameters.AddWithValue("fired", progress.firedAt)     |> ignore
      cmd.Parameters.AddWithValue("body",  body)                 |> ignore
      cmd.ExecuteNonQuery() |> ignore

    member _.List (tenantId : TenantId) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT body FROM pb_runbook_progress \
           WHERE tenant_id = @tid ORDER BY fired_at_ms",
          conn)
      cmd.Parameters.AddWithValue("tid", tid tenantId) |> ignore
      use reader = cmd.ExecuteReader()
      let results = System.Collections.Generic.List<RunbookProgress>()
      while reader.Read() do
        match parseProgress (reader.GetString 0) with
        | Some p -> results.Add p
        | None   -> ()
      results.ToArray()

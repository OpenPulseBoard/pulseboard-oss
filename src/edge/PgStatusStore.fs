module PulseBoard.PgStatusStore

// Postgres-backed IStatusStore (PLAN-NEXT 14.6).
//
// One row per (tenant_id, page_id) in pb_status_page. The whole `StatusPage`
// record is stored as an opaque JSON body using the same `serialisePage` /
// `parsePage` codecs the file store uses, so the on-disk and in-database
// shapes stay in lock-step.

open System
open Npgsql
open PulseBoard.Tenancy
open PulseBoard.StatusPages

let private schema = """
CREATE TABLE IF NOT EXISTS pb_status_page (
  tenant_id  TEXT   NOT NULL,
  page_id    TEXT   NOT NULL,
  slug       TEXT   NOT NULL,
  title      TEXT   NOT NULL,
  body       TEXT   NOT NULL,
  PRIMARY KEY (tenant_id, page_id)
);
CREATE INDEX IF NOT EXISTS pb_status_page_tenant_idx
  ON pb_status_page (tenant_id);
"""

let ensureSchema (connectionString : string) =
  use conn = new NpgsqlConnection(connectionString)
  conn.Open()
  use cmd = new NpgsqlCommand(schema, conn)
  cmd.ExecuteNonQuery() |> ignore

let private tid (TenantId t) = t

type PgStatusStore(connectionString : string) =

  let openConn () =
    let c = new NpgsqlConnection(connectionString)
    c.Open()
    c

  interface IStatusStore with

    member _.List (tenantId : TenantId) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT body FROM pb_status_page \
           WHERE tenant_id = @tid ORDER BY title",
          conn)
      cmd.Parameters.AddWithValue("tid", tid tenantId) |> ignore
      use reader = cmd.ExecuteReader()
      let results = System.Collections.Generic.List<StatusPage>()
      while reader.Read() do
        match parsePage (reader.GetString 0) with
        | Some p -> results.Add p
        | None   -> ()
      results.ToArray()

    member _.TryGet (tenantId : TenantId, pageId : string) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT body FROM pb_status_page \
           WHERE tenant_id = @tid AND page_id = @pid",
          conn)
      cmd.Parameters.AddWithValue("tid", tid tenantId) |> ignore
      cmd.Parameters.AddWithValue("pid", pageId)        |> ignore
      let result = cmd.ExecuteScalar()
      if isNull result then None
      else parsePage (result :?> string)

    member _.Upsert (tenantId : TenantId, page : StatusPage) =
      let body = serialisePage page
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "INSERT INTO pb_status_page (tenant_id, page_id, slug, title, body) \
           VALUES (@tid, @pid, @slug, @title, @body) \
           ON CONFLICT (tenant_id, page_id) DO UPDATE \
             SET slug = EXCLUDED.slug, title = EXCLUDED.title, body = EXCLUDED.body",
          conn)
      cmd.Parameters.AddWithValue("tid",   tid tenantId) |> ignore
      cmd.Parameters.AddWithValue("pid",   page.id)      |> ignore
      cmd.Parameters.AddWithValue("slug",  page.slug)    |> ignore
      cmd.Parameters.AddWithValue("title", page.title)   |> ignore
      cmd.Parameters.AddWithValue("body",  body)         |> ignore
      cmd.ExecuteNonQuery() |> ignore

    member _.Delete (tenantId : TenantId, pageId : string) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "DELETE FROM pb_status_page \
           WHERE tenant_id = @tid AND page_id = @pid",
          conn)
      cmd.Parameters.AddWithValue("tid", tid tenantId) |> ignore
      cmd.Parameters.AddWithValue("pid", pageId)        |> ignore
      cmd.ExecuteNonQuery() > 0

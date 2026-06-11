module PulseBoard.PgRetentionOverrides

open System
open Npgsql
open PulseBoard.Tenancy
open PulseBoard.Retention

// Postgres backing for per-tenant retention overrides. One row per
// tenant; each pillar's TTL is nullable so a row can carry partial
// overrides (e.g. logsMs set, metricsMs falls back to default).
// Schema is owned in-module and applied idempotently via
// `ensureSchema` alongside the other Pg* modules at startup.

let private schema = """
CREATE TABLE IF NOT EXISTS pb_retention_overrides (
  tenant_id   TEXT   NOT NULL PRIMARY KEY REFERENCES pb_tenants(id) ON DELETE CASCADE,
  metrics_ms  BIGINT NULL,
  logs_ms     BIGINT NULL,
  traces_ms   BIGINT NULL
);
"""

let ensureSchema (connectionString : string) =
  use conn = new NpgsqlConnection(connectionString)
  conn.Open()
  use cmd = new NpgsqlCommand(schema, conn)
  cmd.ExecuteNonQuery() |> ignore

type PgRetentionRepo(connectionString : string) =

  let openConn () =
    let c = new NpgsqlConnection(connectionString)
    c.Open()
    c

  let readNullable (r : System.Data.Common.DbDataReader) (ord : int) : int64 option =
    if r.IsDBNull ord then None else Some (r.GetInt64 ord)

  let addNullable (cmd : NpgsqlCommand) (name : string) (v : int64 option) =
    let value : obj =
      match v with
      | Some n -> box n
      | None   -> box DBNull.Value
    cmd.Parameters.AddWithValue(name, value) |> ignore

  interface IRetentionRepo with

    member _.LoadAll () =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT tenant_id, metrics_ms, logs_ms, traces_ms \
           FROM pb_retention_overrides",
          conn)
      use r = cmd.ExecuteReader()
      let acc = ResizeArray<TenantId * RetentionPolicy>()
      while r.Read() do
        let tid = TenantId (r.GetString 0)
        let p =
          { metricsMs = readNullable r 1
            logsMs    = readNullable r 2
            tracesMs  = readNullable r 3 }
        acc.Add(tid, p)
      acc :> seq<_>

    member _.Upsert (TenantId tid, policy) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "INSERT INTO pb_retention_overrides \
             (tenant_id, metrics_ms, logs_ms, traces_ms) \
           VALUES (@tid, @m, @l, @t) \
           ON CONFLICT (tenant_id) DO UPDATE \
             SET metrics_ms = EXCLUDED.metrics_ms, \
                 logs_ms    = EXCLUDED.logs_ms, \
                 traces_ms  = EXCLUDED.traces_ms",
          conn)
      cmd.Parameters.AddWithValue("tid", tid) |> ignore
      addNullable cmd "m" policy.metricsMs
      addNullable cmd "l" policy.logsMs
      addNullable cmd "t" policy.tracesMs
      cmd.ExecuteNonQuery() |> ignore

    member _.Clear (TenantId tid) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "DELETE FROM pb_retention_overrides WHERE tenant_id = @tid",
          conn)
      cmd.Parameters.AddWithValue("tid", tid) |> ignore
      cmd.ExecuteNonQuery() |> ignore

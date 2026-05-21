module PulseBoard.PgAuditLog

open System
open Npgsql
open NpgsqlTypes
open PulseBoard.Tenancy
open PulseBoard.Audit

// Postgres-backed audit trail (PLAN.md Phase 1 step 4). The in-memory
// `InMemoryAuditLog` keeps the recent window for `GET /api/admin/audit`;
// this module is responsible for durability and is the source-of-truth
// the S3 exporter reads from.
//
// `pb_audit_events` is append-only and partitioned only by an index on
// `ts`; row volume per tenant is modest (a handful of writes per request),
// so a single table is fine until Phase 6 reliability work.
//
// `pb_audit_exports` is the high-water mark used by the S3 exporter to
// stay idempotent across restarts. One row per UTC calendar day; days
// with zero events still get a row so the exporter doesn't re-scan.

let private schema = """
CREATE TABLE IF NOT EXISTS pb_audit_events (
  id          BIGSERIAL    PRIMARY KEY,
  ts          TIMESTAMPTZ  NOT NULL,
  tenant_id   TEXT         NULL,
  api_key_id  TEXT         NULL,
  action      TEXT         NOT NULL,
  resource    TEXT         NOT NULL,
  outcome     TEXT         NOT NULL,
  remote_ip   TEXT         NULL,
  details     TEXT         NULL
);
CREATE INDEX IF NOT EXISTS pb_audit_events_ts_idx ON pb_audit_events(ts);

CREATE TABLE IF NOT EXISTS pb_audit_exports (
  day          DATE         PRIMARY KEY,
  exported_at  TIMESTAMPTZ  NOT NULL,
  object_key   TEXT         NOT NULL,
  rows         BIGINT       NOT NULL,
  bytes        BIGINT       NOT NULL
);
"""

let ensureSchema (connectionString : string) =
  use conn = new NpgsqlConnection(connectionString)
  conn.Open()
  use cmd = new NpgsqlCommand(schema, conn)
  cmd.ExecuteNonQuery() |> ignore

let private outcomeText = function
  | Allow -> "allow"
  | Deny  -> "deny"
  | Error -> "error"

let private parseOutcome (s : string) =
  match s with
  | "allow" -> Allow
  | "deny"  -> Deny
  | _       -> Error

/// `IAuditLog` whose `Append` writes one row to `pb_audit_events`. `Tail`
/// returns `[||]` — paged reads belong to the in-memory ring composed in
/// front of this sink. Append failures are caught and silently dropped to
/// match the contract of the other sinks (the alternative would be to
/// surface auth/ingest exceptions on Postgres outages).
type PgAuditLog (connectionString : string) =
  interface IAuditLog with
    member _.Append ev =
      try
        use conn = new NpgsqlConnection(connectionString)
        conn.Open()
        use cmd =
          new NpgsqlCommand(
            "INSERT INTO pb_audit_events \
             (ts, tenant_id, api_key_id, action, resource, outcome, remote_ip, details) \
             VALUES (@ts, @tid, @kid, @act, @res, @out, @ip, @det)", conn)
        let pStr (name : string) (v : string option) =
          let o : obj = match v with Some s -> box s | None -> box DBNull.Value
          cmd.Parameters.AddWithValue(name, o) |> ignore
        // Bind `ts` as TIMESTAMPTZ explicitly so Npgsql doesn't reinterpret
        // the DateTime in local time.
        let tsParam = cmd.Parameters.Add("ts", NpgsqlDbType.TimestampTz)
        tsParam.Value <- ev.ts.UtcDateTime
        pStr "tid" (ev.tenant   |> Option.map (fun (TenantId t) -> t))
        pStr "kid" (ev.apiKeyId |> Option.map (fun (ApiKeyId k) -> k))
        cmd.Parameters.AddWithValue("act", ev.action)   |> ignore
        cmd.Parameters.AddWithValue("res", ev.resource) |> ignore
        cmd.Parameters.AddWithValue("out", outcomeText ev.outcome) |> ignore
        pStr "ip"  ev.remoteIp
        pStr "det" ev.details
        cmd.ExecuteNonQuery() |> ignore
      with _ -> ()
    member _.Tail _ = [||]

/// Read events whose `ts` falls in `[fromTs, untilTs)` (UTC) in ascending
/// (ts, id) order. Materialises into an array so the caller can dispose
/// the connection before doing slow work (S3 upload).
let readWindow (connectionString : string)
               (fromTs : DateTime) (untilTs : DateTime) : AuditEvent[] =
  use conn = new NpgsqlConnection(connectionString)
  conn.Open()
  use cmd =
    new NpgsqlCommand(
      "SELECT ts, tenant_id, api_key_id, action, resource, outcome, remote_ip, details \
       FROM pb_audit_events WHERE ts >= @f AND ts < @u ORDER BY ts, id", conn)
  let pf = cmd.Parameters.Add("f", NpgsqlDbType.TimestampTz)
  pf.Value <- DateTime.SpecifyKind(fromTs, DateTimeKind.Utc)
  let pu = cmd.Parameters.Add("u", NpgsqlDbType.TimestampTz)
  pu.Value <- DateTime.SpecifyKind(untilTs, DateTimeKind.Utc)
  use r = cmd.ExecuteReader()
  let acc = ResizeArray<AuditEvent>()
  while r.Read() do
    let opt (i : int) = if r.IsDBNull i then None else Some (r.GetString i)
    acc.Add
      { ts       = DateTimeOffset(r.GetDateTime 0, TimeSpan.Zero)
        tenant   = opt 1 |> Option.map TenantId
        apiKeyId = opt 2 |> Option.map ApiKeyId
        action   = r.GetString 3
        resource = r.GetString 4
        outcome  = parseOutcome (r.GetString 5)
        remoteIp = opt 6
        details  = opt 7 }
  acc.ToArray()

/// UTC calendar days strictly before today that contain at least one
/// audit event but have no row in `pb_audit_exports`. Ordered ascending
/// so the exporter processes the oldest backlog first.
let pendingExportDays (connectionString : string) : DateTime[] =
  use conn = new NpgsqlConnection(connectionString)
  conn.Open()
  use cmd =
    new NpgsqlCommand(
      "SELECT DISTINCT (date_trunc('day', ts AT TIME ZONE 'UTC'))::date AS d \
       FROM pb_audit_events ae \
       WHERE ts < date_trunc('day', now() AT TIME ZONE 'UTC') \
         AND NOT EXISTS ( \
           SELECT 1 FROM pb_audit_exports e \
           WHERE e.day = (date_trunc('day', ae.ts AT TIME ZONE 'UTC'))::date) \
       ORDER BY d", conn)
  use r = cmd.ExecuteReader()
  let acc = ResizeArray<DateTime>()
  while r.Read() do
    acc.Add(DateTime.SpecifyKind(r.GetDateTime 0, DateTimeKind.Utc))
  acc.ToArray()

/// Mark `day` as exported. ON CONFLICT DO NOTHING so a concurrent or
/// duplicate exporter run is a no-op rather than an error.
let recordExport (connectionString : string)
                 (day : DateTime) (objectKey : string)
                 (rows : int64) (bytes : int64) =
  use conn = new NpgsqlConnection(connectionString)
  conn.Open()
  use cmd =
    new NpgsqlCommand(
      "INSERT INTO pb_audit_exports (day, exported_at, object_key, rows, bytes) \
       VALUES (@d, now() AT TIME ZONE 'UTC', @k, @r, @b) \
       ON CONFLICT (day) DO NOTHING", conn)
  // Bind as DATE explicitly via DateOnly so TZ doesn't shift the value.
  let pd = cmd.Parameters.Add("d", NpgsqlDbType.Date)
  pd.Value <- DateOnly.FromDateTime(day.Date)
  cmd.Parameters.AddWithValue("k", objectKey) |> ignore
  cmd.Parameters.AddWithValue("r", rows)      |> ignore
  cmd.Parameters.AddWithValue("b", bytes)     |> ignore
  cmd.ExecuteNonQuery() |> ignore

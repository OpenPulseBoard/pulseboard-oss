module PulseBoard.PgNotifyQueue

// Postgres-backed INotifyQueue. Replaces the NDJSON journal with a proper
// queue table. Leasing uses SELECT FOR UPDATE SKIP LOCKED inside a CTE so
// concurrent workers never double-deliver the same message.
//
// Schema has one row per message. Mutable state columns (attempt,
// next_run_at_ms, last_error, leased_until_ms, is_dead, dead_at_ms,
// dead_reason) are updated in-place; Map<string,string> fields (headers,
// extra) are stored as compact JSON TEXT.

open System
open System.IO
open System.Text
open System.Text.Json
open Npgsql
open PulseBoard.Tenancy
open PulseBoard.NotifyQueue

let private schema = """
CREATE TABLE IF NOT EXISTS pb_notify_queue (
  id              TEXT    PRIMARY KEY,
  tenant_id       TEXT    NOT NULL,
  receiver_id     TEXT    NOT NULL,
  receiver_type   TEXT    NOT NULL,
  url             TEXT    NOT NULL,
  secret          TEXT,
  body            TEXT    NOT NULL,
  headers_json    TEXT    NOT NULL DEFAULT '{}',
  extra_json      TEXT    NOT NULL DEFAULT '{}',
  attempt         INT     NOT NULL DEFAULT 0,
  max_attempts    INT     NOT NULL DEFAULT 5,
  enqueued_at_ms  BIGINT  NOT NULL,
  next_run_at_ms  BIGINT  NOT NULL,
  last_error      TEXT,
  leased_until_ms BIGINT,
  is_dead         BOOLEAN NOT NULL DEFAULT FALSE,
  dead_at_ms      BIGINT,
  dead_reason     TEXT
);
CREATE INDEX IF NOT EXISTS pb_notify_queue_live_idx
  ON pb_notify_queue (next_run_at_ms)
  WHERE is_dead = FALSE;
CREATE INDEX IF NOT EXISTS pb_notify_queue_tenant_idx
  ON pb_notify_queue (tenant_id);
"""

let ensureSchema (connectionString : string) =
  use conn = new NpgsqlConnection(connectionString)
  conn.Open()
  use cmd = new NpgsqlCommand(schema, conn)
  cmd.ExecuteNonQuery() |> ignore

let private nowMs () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

let private tid (TenantId t) = t

/// Bind a nullable TEXT parameter. A bare CLR `null` makes Npgsql throw
/// ("must have either its NpgsqlDbType or its DataTypeName or its Value
/// set") because it cannot infer the column type from null, so map None
/// to DBNull.Value instead.
let private dbStr (s : string option) : obj =
  match s with
  | Some v -> box v
  | None   -> box DBNull.Value

let private serialiseMap (m : Map<string,string>) : string =
  use ms = new MemoryStream()
  use w = new Utf8JsonWriter(ms)
  w.WriteStartObject()
  for kvp in m do w.WriteString(kvp.Key, kvp.Value)
  w.WriteEndObject()
  w.Flush()
  Encoding.UTF8.GetString(ms.ToArray())

let private parseMap (json : string) : Map<string,string> =
  if String.IsNullOrEmpty json then Map.empty
  else
    try
      use doc = JsonDocument.Parse json
      doc.RootElement.EnumerateObject()
      |> Seq.choose (fun p ->
        if p.Value.ValueKind = JsonValueKind.String then Some (p.Name, p.Value.GetString())
        else None)
      |> Map.ofSeq
    with _ -> Map.empty

// SELECT columns in order for readMsg / readDeadLetter
let private msgCols =
  "id, tenant_id, receiver_id, receiver_type, url, secret, body, \
   headers_json, extra_json, attempt, max_attempts, enqueued_at_ms, \
   next_run_at_ms, last_error"

// Same columns qualified with the `q` table alias. Required by the Lease
// query's RETURNING clause: that UPDATE has both `pb_notify_queue q` and
// the `selected` CTE in scope, so a bare `id` (etc.) is ambiguous
// (Postgres 42702). readMsg reads positionally, so order must match msgCols.
let private msgColsQ =
  msgCols.Split(',')
  |> Array.map (fun c -> "q." + c.Trim())
  |> String.concat ", "

let private readMsg (r : System.Data.Common.DbDataReader) : OutboundMessage =
  { id           = r.GetString 0
    tenantId     = TenantId (r.GetString 1)
    receiverId   = r.GetString 2
    receiverType = r.GetString 3
    url          = r.GetString 4
    secret       = if r.IsDBNull 5  then None else Some (r.GetString 5)
    body         = r.GetString 6
    headers      = parseMap (r.GetString 7)
    extra        = parseMap (r.GetString 8)
    attempt      = r.GetInt32 9
    maxAttempts  = r.GetInt32 10
    enqueuedAt   = r.GetInt64 11
    nextRunAt    = r.GetInt64 12
    lastError    = if r.IsDBNull 13 then None else Some (r.GetString 13) }

// dead_at_ms=14, dead_reason=15 appended after msgCols
let private readDeadLetter (r : System.Data.Common.DbDataReader) : DeadLetter =
  { msg    = readMsg r
    deadAt = r.GetInt64 14
    reason = if r.IsDBNull 15 then "" else r.GetString 15 }

// Lease duration: 5 minutes. Expired leases are re-eligible for pickup.
let private leaseDurationMs = 5L * 60L * 1000L

type PgNotifyQueue(connectionString : string) =

  let openConn () =
    let c = new NpgsqlConnection(connectionString)
    c.Open()
    c

  interface INotifyQueue with

    member _.Enqueue (m : OutboundMessage) =
      let (TenantId tenantId) = m.tenantId
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "INSERT INTO pb_notify_queue \
           (id, tenant_id, receiver_id, receiver_type, url, secret, body, \
            headers_json, extra_json, attempt, max_attempts, enqueued_at_ms, next_run_at_ms, last_error) \
           VALUES (@id, @tid, @rid, @rt, @url, @sec, @body, @hdr, @ext, \
                   @att, @max, @enq, @nxt, @err) \
           ON CONFLICT (id) DO NOTHING",
          conn)
      cmd.Parameters.AddWithValue("id",   m.id)                               |> ignore
      cmd.Parameters.AddWithValue("tid",  tenantId)                           |> ignore
      cmd.Parameters.AddWithValue("rid",  m.receiverId)                       |> ignore
      cmd.Parameters.AddWithValue("rt",   m.receiverType)                     |> ignore
      cmd.Parameters.AddWithValue("url",  m.url)                              |> ignore
      cmd.Parameters.AddWithValue("sec",  dbStr m.secret)                     |> ignore
      cmd.Parameters.AddWithValue("body", m.body)                             |> ignore
      cmd.Parameters.AddWithValue("hdr",  serialiseMap m.headers)             |> ignore
      cmd.Parameters.AddWithValue("ext",  serialiseMap m.extra)               |> ignore
      cmd.Parameters.AddWithValue("att",  m.attempt)                          |> ignore
      cmd.Parameters.AddWithValue("max",  m.maxAttempts)                      |> ignore
      cmd.Parameters.AddWithValue("enq",  m.enqueuedAt)                       |> ignore
      cmd.Parameters.AddWithValue("nxt",  m.nextRunAt)                        |> ignore
      cmd.Parameters.AddWithValue("err",  dbStr m.lastError)                  |> ignore
      cmd.ExecuteNonQuery() |> ignore

    member _.Lease (batchSize : int, nowMs : int64) =
      let leasedUntil = nowMs + leaseDurationMs
      use conn = openConn ()
      use tx   = conn.BeginTransaction()
      use cmd  =
        new NpgsqlCommand(
          "WITH selected AS ( \
             SELECT id FROM pb_notify_queue \
             WHERE is_dead = FALSE \
               AND next_run_at_ms <= @now \
               AND (leased_until_ms IS NULL OR leased_until_ms <= @now) \
             ORDER BY next_run_at_ms \
             LIMIT @n \
             FOR UPDATE SKIP LOCKED \
           ) \
           UPDATE pb_notify_queue q \
           SET leased_until_ms = @lu \
           FROM selected \
           WHERE q.id = selected.id \
           RETURNING " + msgColsQ,
          conn, tx)
      cmd.Parameters.AddWithValue("now", nowMs)      |> ignore
      cmd.Parameters.AddWithValue("n",   batchSize)  |> ignore
      cmd.Parameters.AddWithValue("lu",  leasedUntil)|> ignore
      use reader = cmd.ExecuteReader()
      let results = System.Collections.Generic.List<OutboundMessage>()
      while reader.Read() do results.Add(readMsg reader)
      reader.Close()
      tx.Commit()
      results.ToArray()

    member _.Ack (id : string) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "DELETE FROM pb_notify_queue WHERE id = @id AND is_dead = FALSE",
          conn)
      cmd.Parameters.AddWithValue("id", id) |> ignore
      cmd.ExecuteNonQuery() |> ignore

    member _.Fail (id : string, err : string, nextRunAt : int64) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "UPDATE pb_notify_queue \
           SET attempt = attempt + 1, last_error = @err, next_run_at_ms = @nxt, leased_until_ms = NULL \
           WHERE id = @id AND is_dead = FALSE",
          conn)
      cmd.Parameters.AddWithValue("err", err)       |> ignore
      cmd.Parameters.AddWithValue("nxt", nextRunAt) |> ignore
      cmd.Parameters.AddWithValue("id",  id)        |> ignore
      cmd.ExecuteNonQuery() |> ignore

    member _.Dead (id : string, reason : string) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "UPDATE pb_notify_queue \
           SET is_dead = TRUE, dead_at_ms = @dat, dead_reason = @rsn, leased_until_ms = NULL \
           WHERE id = @id AND is_dead = FALSE",
          conn)
      cmd.Parameters.AddWithValue("dat", nowMs ()) |> ignore
      cmd.Parameters.AddWithValue("rsn", reason)   |> ignore
      cmd.Parameters.AddWithValue("id",  id)       |> ignore
      cmd.ExecuteNonQuery() |> ignore

    member _.Pending (tenantId : TenantId option) =
      use conn = openConn ()
      let sql, parms =
        match tenantId with
        | Some t ->
          "SELECT " + msgCols + " FROM pb_notify_queue \
           WHERE is_dead = FALSE AND tenant_id = @tid ORDER BY next_run_at_ms",
          [| "tid", box (tid t) |]
        | None ->
          "SELECT " + msgCols + " FROM pb_notify_queue \
           WHERE is_dead = FALSE ORDER BY next_run_at_ms",
          [||]
      use cmd = new NpgsqlCommand(sql, conn)
      for name, value in parms do
        cmd.Parameters.AddWithValue(name, value) |> ignore
      use reader = cmd.ExecuteReader()
      let results = System.Collections.Generic.List<OutboundMessage>()
      while reader.Read() do results.Add(readMsg reader)
      results.ToArray()

    member _.DeadLetters (tenantId : TenantId option) =
      use conn = openConn ()
      let sql, parms =
        match tenantId with
        | Some t ->
          "SELECT " + msgCols + ", dead_at_ms, dead_reason FROM pb_notify_queue \
           WHERE is_dead = TRUE AND tenant_id = @tid ORDER BY dead_at_ms DESC",
          [| "tid", box (tid t) |]
        | None ->
          "SELECT " + msgCols + ", dead_at_ms, dead_reason FROM pb_notify_queue \
           WHERE is_dead = TRUE ORDER BY dead_at_ms DESC",
          [||]
      use cmd = new NpgsqlCommand(sql, conn)
      for name, value in parms do
        cmd.Parameters.AddWithValue(name, value) |> ignore
      use reader = cmd.ExecuteReader()
      let results = System.Collections.Generic.List<DeadLetter>()
      while reader.Read() do results.Add(readDeadLetter reader)
      results.ToArray()

    member _.ReplayDead (id : string) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "UPDATE pb_notify_queue \
           SET is_dead = FALSE, dead_at_ms = NULL, dead_reason = NULL, \
               attempt = 0, last_error = NULL, next_run_at_ms = @now, leased_until_ms = NULL \
           WHERE id = @id AND is_dead = TRUE",
          conn)
      cmd.Parameters.AddWithValue("now", nowMs ()) |> ignore
      cmd.Parameters.AddWithValue("id",  id)       |> ignore
      cmd.ExecuteNonQuery() = 1

    member _.PurgeDead (id : string) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "DELETE FROM pb_notify_queue WHERE id = @id AND is_dead = TRUE",
          conn)
      cmd.Parameters.AddWithValue("id", id) |> ignore
      cmd.ExecuteNonQuery() = 1

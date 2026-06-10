module PulseBoard.PgSpanStore

// Postgres-backed ISpanStore.
//
// We keep one row per span keyed by (tenant_id, trace_id, span_id), then
// derive trace summaries and service maps from query snapshots just like the
// in-memory implementation does.

open System
open System.Text
open System.Text.Json
open Npgsql
open PulseBoard.Tenancy
open PulseBoard.Spans

let private schema = """
CREATE TABLE IF NOT EXISTS pb_span (
  tenant_id       TEXT   NOT NULL,
  trace_id        TEXT   NOT NULL,
  span_id         TEXT   NOT NULL,
  parent_span_id  TEXT   NOT NULL,
  service         TEXT   NOT NULL,
  operation       TEXT   NOT NULL,
  kind            INT    NOT NULL,
  start_ms        BIGINT NOT NULL,
  end_ms          BIGINT NOT NULL,
  status_code     INT    NOT NULL,
  attributes_json TEXT   NOT NULL,
  PRIMARY KEY (tenant_id, trace_id, span_id)
);
CREATE INDEX IF NOT EXISTS pb_span_tenant_end_idx
  ON pb_span (tenant_id, end_ms DESC);
CREATE INDEX IF NOT EXISTS pb_span_tenant_trace_idx
  ON pb_span (tenant_id, trace_id, start_ms);
"""

let ensureSchema (connectionString : string) =
  use conn = new NpgsqlConnection(connectionString)
  conn.Open()
  use cmd = new NpgsqlCommand(schema, conn)
  cmd.ExecuteNonQuery() |> ignore

let private tid (TenantId t) = t

let private attrsToJson (attrs : Map<string, string>) =
  use ms = new IO.MemoryStream()
  use w = new Utf8JsonWriter(ms)
  w.WriteStartObject()
  for KeyValue(k, v) in attrs do
    w.WriteString(k, v)
  w.WriteEndObject()
  w.Flush()
  Encoding.UTF8.GetString(ms.ToArray())

let private attrsOfJson (raw : string) : Map<string, string> =
  if String.IsNullOrWhiteSpace raw then
    Map.empty
  else
    try
      use doc = JsonDocument.Parse(raw)
      if doc.RootElement.ValueKind <> JsonValueKind.Object then
        Map.empty
      else
        doc.RootElement.EnumerateObject()
        |> Seq.map (fun p -> p.Name, p.Value.GetString() |> Option.ofObj |> Option.defaultValue "")
        |> Map.ofSeq
    with _ ->
      Map.empty

let private spanOfReader (r : System.Data.Common.DbDataReader) : Span =
  { traceId      = r.GetString 0
    spanId       = r.GetString 1
    parentSpanId = r.GetString 2
    service      = r.GetString 3
    operation    = r.GetString 4
    kind         = kindOfInt (r.GetInt32 5)
    startMs      = r.GetInt64 6
    endMs        = r.GetInt64 7
    statusCode   = r.GetInt32 8
    attributes   = attrsOfJson (r.GetString 9) }

type PgSpanStore(connectionString : string) =

  let openConn () =
    let c = new NpgsqlConnection(connectionString)
    c.Open()
    c

  let readSpans (sql : string) (bind : NpgsqlCommand -> unit) : Span array =
    use conn = openConn ()
    use cmd = new NpgsqlCommand(sql, conn)
    bind cmd
    use reader = cmd.ExecuteReader()
    let xs = System.Collections.Generic.List<Span>()
    while reader.Read() do
      xs.Add (spanOfReader reader)
    xs.ToArray()

  interface ISpanStore with

    member _.Ingest (tenantId : TenantId, spans : Span seq) =
      use conn = openConn ()
      use tx = conn.BeginTransaction()
      use cmd =
        new NpgsqlCommand(
          "INSERT INTO pb_span \
           (tenant_id, trace_id, span_id, parent_span_id, service, operation, kind, start_ms, end_ms, status_code, attributes_json) \
           VALUES (@tid, @trace, @span, @parent, @service, @op, @kind, @start, @end, @status, @attrs) \
           ON CONFLICT (tenant_id, trace_id, span_id) DO UPDATE SET \
             parent_span_id = EXCLUDED.parent_span_id, \
             service = EXCLUDED.service, \
             operation = EXCLUDED.operation, \
             kind = EXCLUDED.kind, \
             start_ms = EXCLUDED.start_ms, \
             end_ms = EXCLUDED.end_ms, \
             status_code = EXCLUDED.status_code, \
             attributes_json = EXCLUDED.attributes_json",
          conn, tx)
      let pTid    = cmd.Parameters.AddWithValue("tid", "")
      let pTrace  = cmd.Parameters.AddWithValue("trace", "")
      let pSpan   = cmd.Parameters.AddWithValue("span", "")
      let pParent = cmd.Parameters.AddWithValue("parent", "")
      let pSvc    = cmd.Parameters.AddWithValue("service", "")
      let pOp     = cmd.Parameters.AddWithValue("op", "")
      let pKind   = cmd.Parameters.AddWithValue("kind", 0)
      let pStart  = cmd.Parameters.AddWithValue("start", 0L)
      let pEnd    = cmd.Parameters.AddWithValue("end", 0L)
      let pStatus = cmd.Parameters.AddWithValue("status", 0)
      let pAttrs  = cmd.Parameters.AddWithValue("attrs", "{}")
      let t = tid tenantId
      for s in spans do
        pTid.Value    <- t
        pTrace.Value  <- s.traceId
        pSpan.Value   <- s.spanId
        pParent.Value <- s.parentSpanId
        pSvc.Value    <- s.service
        pOp.Value     <- s.operation
        pKind.Value   <-
          match s.kind with
          | KindInternal -> 1
          | KindServer -> 2
          | KindClient -> 3
          | KindProducer -> 4
          | KindConsumer -> 5
          | _ -> 0
        pStart.Value  <- s.startMs
        pEnd.Value    <- s.endMs
        pStatus.Value <- s.statusCode
        pAttrs.Value  <- attrsToJson s.attributes
        cmd.ExecuteNonQuery() |> ignore
      tx.Commit()

    member _.Traces (tenantId : TenantId, sinceMs : int64, limit : int) =
      let cappedLimit = max 1 limit
      readSpans
        "SELECT trace_id, span_id, parent_span_id, service, operation, kind, start_ms, end_ms, status_code, attributes_json \
         FROM pb_span \
         WHERE tenant_id = @tid AND end_ms >= @since"
        (fun cmd ->
          cmd.Parameters.AddWithValue("tid", tid tenantId) |> ignore
          cmd.Parameters.AddWithValue("since", sinceMs)    |> ignore)
      |> Array.groupBy (fun s -> s.traceId)
      |> Array.map (fun (_, xs) -> summarise xs)
      |> Array.sortByDescending (fun t -> t.startMs)
      |> fun xs -> if xs.Length <= cappedLimit then xs else xs.[.. cappedLimit - 1]

    member _.GetTrace (tenantId : TenantId, traceId : string) =
      readSpans
        "SELECT trace_id, span_id, parent_span_id, service, operation, kind, start_ms, end_ms, status_code, attributes_json \
         FROM pb_span \
         WHERE tenant_id = @tid AND trace_id = @trace \
         ORDER BY start_ms"
        (fun cmd ->
          cmd.Parameters.AddWithValue("tid", tid tenantId) |> ignore
          cmd.Parameters.AddWithValue("trace", traceId)    |> ignore)

    member _.Map (tenantId : TenantId, sinceMs : int64) =
      readSpans
        "SELECT trace_id, span_id, parent_span_id, service, operation, kind, start_ms, end_ms, status_code, attributes_json \
         FROM pb_span \
         WHERE tenant_id = @tid AND end_ms >= @since"
        (fun cmd ->
          cmd.Parameters.AddWithValue("tid", tid tenantId) |> ignore
          cmd.Parameters.AddWithValue("since", sinceMs)    |> ignore)
      |> fun spans -> buildMap spans sinceMs

    member _.PruneOlderThan (cutoffMs : int64) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "DELETE FROM pb_span WHERE end_ms < @cutoff",
          conn)
      cmd.Parameters.AddWithValue("cutoff", cutoffMs) |> ignore
      cmd.ExecuteNonQuery()

    member _.Count (tenantId : TenantId) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT COUNT(*) FROM pb_span WHERE tenant_id = @tid",
          conn)
      cmd.Parameters.AddWithValue("tid", tid tenantId) |> ignore
      int (cmd.ExecuteScalar() :?> int64)

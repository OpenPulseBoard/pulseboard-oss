module PulseBoard.TraceApi

open System
open System.Text
open System.Text.Json
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open PulseBoard.Tenancy
open PulseBoard.Spans

// REST surface for the Traces / Service Map tabs.
// All routes are read-only and tenant-scoped via the same Rbac gate
// as `Dashboards.fs`. Span ingest happens upstream in `Otlp.traces`.

let singleTenantId = TenantId "__local__"

let private resolveTenant (multiTenant : bool) (ctx : HttpContext) : TenantId option =
  if multiTenant then
    PulseBoard.Rbac.tryGetTenant ctx |> Option.map (fun t -> t.tenant.id)
  else
    Some singleTenantId

let private jsonOk (body : string) : WebPart =
  OK body >=> Writers.setMimeType "application/json"

let private errJson (status : int) (msg : string) : WebPart =
  let body = sprintf """{"error":%s}""" (JsonSerializer.Serialize msg)
  let writer =
    match status with
    | 400 -> BAD_REQUEST
    | 401 -> Suave.RequestErrors.UNAUTHORIZED
    | 404 -> NOT_FOUND
    | _   -> Suave.ServerErrors.INTERNAL_ERROR
  writer body >=> Writers.setMimeType "application/json"

let private writeMap (w : Utf8JsonWriter) (m : Map<string,string>) =
  w.WriteStartObject()
  for KeyValue(k, v) in m do w.WriteString(k, v)
  w.WriteEndObject()

let private writeSpan (w : Utf8JsonWriter) (s : Span) =
  w.WriteStartObject()
  w.WriteString("traceId",      s.traceId)
  w.WriteString("spanId",       s.spanId)
  w.WriteString("parentSpanId", s.parentSpanId)
  w.WriteString("service",      s.service)
  w.WriteString("operation",    s.operation)
  w.WriteString("kind",         kindName s.kind)
  w.WriteNumber("startMs",      s.startMs)
  w.WriteNumber("endMs",        s.endMs)
  w.WriteNumber("durationMs",   duration s)
  w.WriteNumber("statusCode",   s.statusCode)
  w.WriteBoolean("error",       isError s)
  w.WritePropertyName "attributes"
  writeMap w s.attributes
  w.WriteEndObject()

let private writeSummary (w : Utf8JsonWriter) (t : TraceSummary) =
  w.WriteStartObject()
  w.WriteString("traceId",       t.traceId)
  w.WriteString("rootService",   t.rootService)
  w.WriteString("rootOperation", t.rootOperation)
  w.WriteNumber("startMs",       t.startMs)
  w.WriteNumber("durationMs",    t.durationMs)
  w.WriteNumber("spanCount",     t.spanCount)
  w.WriteNumber("errorCount",    t.errorCount)
  w.WritePropertyName "services"
  w.WriteStartArray()
  for s in t.services do w.WriteStringValue s
  w.WriteEndArray()
  w.WriteEndObject()

let private serialise (write : Utf8JsonWriter -> unit) : string =
  use ms = new IO.MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    write w
  )
  Encoding.UTF8.GetString(ms.ToArray())

let private parseInt (q : HttpRequest) (name : string) (dflt : int) : int =
  match q.queryParam name with
  | Choice1Of2 v ->
    let mutable n = 0
    if Int32.TryParse(v, &n) then n else dflt
  | _ -> dflt

let private parseInt64 (q : HttpRequest) (name : string) (dflt : int64) : int64 =
  match q.queryParam name with
  | Choice1Of2 v ->
    let mutable n = 0L
    if Int64.TryParse(v, &n) then n else dflt
  | _ -> dflt

/// Build the public REST surface for traces + service map. `multiTenant`
/// gates whether the active tenant is read from the auth context
/// (multi-tenant) or pinned to `__local__` (single-tenant).
let webPart (multiTenant : bool) (store : ISpanStore) : WebPart =

  let withTenant (handler : TenantId -> WebPart) : WebPart =
    fun ctx -> async {
      match resolveTenant multiTenant ctx with
      | None     -> return! errJson 401 "no tenant in request" ctx
      | Some tid -> return! handler tid ctx
    }

  let listTraces : WebPart =
    withTenant (fun tid ->
      fun ctx -> async {
        let nowMs   = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        let sinceMs =
          let s = parseInt64 ctx.request "sinceMs" 0L
          if s > 0L then s
          else nowMs - int64 (parseInt ctx.request "windowSec" 3600) * 1000L
        let limit = max 1 (min 1000 (parseInt ctx.request "limit" 100))
        let traces = store.Traces(tid, sinceMs, limit)
        let body =
          serialise (fun w ->
            w.WriteStartArray()
            for t in traces do writeSummary w t
            w.WriteEndArray())
        return! jsonOk body ctx
      })

  let getTrace (traceId : string) : WebPart =
    withTenant (fun tid ->
      fun ctx -> async {
        let spans = store.GetTrace(tid, traceId)
        if spans.Length = 0 then
          return! errJson 404 ("no spans for trace " + traceId) ctx
        else
          let summary = Spans.summarise spans
          let body =
            serialise (fun w ->
              w.WriteStartObject()
              w.WritePropertyName "summary"
              writeSummary w summary
              w.WritePropertyName "spans"
              w.WriteStartArray()
              for s in spans do writeSpan w s
              w.WriteEndArray()
              w.WriteEndObject())
          return! jsonOk body ctx
      })

  let getMap : WebPart =
    withTenant (fun tid ->
      fun ctx -> async {
        let nowMs   = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        let sinceMs =
          let s = parseInt64 ctx.request "sinceMs" 0L
          if s > 0L then s
          else nowMs - int64 (parseInt ctx.request "windowSec" 3600) * 1000L
        let m = store.Map(tid, sinceMs)
        let body =
          serialise (fun w ->
            w.WriteStartObject()
            w.WriteNumber("sinceMs",     m.sinceMs)
            w.WriteNumber("generatedMs", m.generatedMs)
            w.WritePropertyName "nodes"
            w.WriteStartArray()
            for n in m.nodes do
              w.WriteStartObject()
              w.WriteString("service",    n.service)
              w.WriteNumber("spanCount",  n.spanCount)
              w.WriteNumber("errorCount", n.errorCount)
              w.WriteNumber("p50Ms",      n.p50Ms)
              w.WriteNumber("p95Ms",      n.p95Ms)
              w.WriteNumber("p99Ms",      n.p99Ms)
              w.WriteEndObject()
            w.WriteEndArray()
            w.WritePropertyName "edges"
            w.WriteStartArray()
            for e in m.edges do
              w.WriteStartObject()
              w.WriteString("from",       e.fromService)
              w.WriteString("to",         e.toService)
              w.WriteNumber("callCount",  e.callCount)
              w.WriteNumber("errorCount", e.errorCount)
              w.WriteNumber("p50Ms",      e.p50Ms)
              w.WriteNumber("p95Ms",      e.p95Ms)
              w.WriteNumber("p99Ms",      e.p99Ms)
              w.WriteEndObject()
            w.WriteEndArray()
            w.WriteEndObject())
        return! jsonOk body ctx
      })

  choose [
    GET >=> path        "/api/traces"            >=> listTraces
    GET >=> pathScan    "/api/traces/%s"             getTrace
    GET >=> path        "/api/servicemap"        >=> getMap
  ]

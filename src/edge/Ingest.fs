module PulseBoard.Ingest

open System
open System.Text
open System.Text.Json
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open PulseBoard.TimeSeries
open PulseBoard.Hub
open PulseBoard.Tenancy
open PulseBoard.Quotas
open PulseBoard.Audit

let private readBody (ctx : HttpContext) : string =
  Encoding.UTF8.GetString ctx.request.rawForm

let private tryGetString (el : JsonElement) (name : string) : string option =
  match el.TryGetProperty name with
  | true, p when p.ValueKind = JsonValueKind.String -> Some (p.GetString())
  | _ -> None

let private tryGetDouble (el : JsonElement) (name : string) : float option =
  match el.TryGetProperty name with
  | true, p when p.ValueKind = JsonValueKind.Number ->
    let ok, v = p.TryGetDouble()
    if ok then Some v else None
  | _ -> None

let private tryGetInt64 (el : JsonElement) (name : string) : int64 option =
  match el.TryGetProperty name with
  | true, p when p.ValueKind = JsonValueKind.Number ->
    let ok, v = p.TryGetInt64()
    if ok then Some v else None
  | _ -> None

let private publishMetric (hub : Broadcaster) (name : string) (p : Point) =
  let json =
    sprintf """{"type":"metric","name":%s,"ts":%d,"value":%s}"""
      (JsonSerializer.Serialize name)
      p.ts
      (p.value.ToString(System.Globalization.CultureInfo.InvariantCulture))
  hub.Publish json

let private publishLog (hub : Broadcaster) (e : LogEntry) =
  let json =
    sprintf """{"type":"log","ts":%d,"service":%s,"level":%s,"message":%s}"""
      e.ts
      (JsonSerializer.Serialize e.service)
      (JsonSerializer.Serialize e.level)
      (JsonSerializer.Serialize e.message)
  hub.Publish json

let private parseRootAsArray (body : string) : JsonElement array =
  use doc = JsonDocument.Parse body
  let root = doc.RootElement
  match root.ValueKind with
  | JsonValueKind.Array  -> root.EnumerateArray() |> Seq.map (fun e -> e.Clone()) |> Seq.toArray
  | JsonValueKind.Object -> [| root.Clone() |]
  | _ -> [||]

let private parseNdjson (body : string) : JsonElement array =
  body.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
  |> Array.choose (fun line ->
      let l = line.Trim()
      if l.Length = 0 then None
      else
        let doc = JsonDocument.Parse l
        Some (doc.RootElement.Clone()))

let private isNdjson (body : string) =
  let trimmed = body.TrimStart()
  trimmed.StartsWith "{" && body.Contains '\n'

/// Hook for per-tenant quota enforcement at the ingest edge. When `None`
/// the handlers behave exactly as in single-tenant mode (no cardinality
/// admission, no log-byte charging). Passed `Some` from the multi-tenant
/// wiring in `Program.fs`.
[<NoComparison; NoEquality>]
type IngestQuotas =
  { limiter  : Limiter
    auditLog : IAuditLog }

let private emitQuotaDeny (q : IngestQuotas) (ctx : HttpContext)
                          (action : string) (details : string) =
  let t = PulseBoard.Rbac.tryGetTenant ctx
  let ev : AuditEvent =
    { ts       = DateTimeOffset.UtcNow
      tenant   = t |> Option.map (fun x -> x.tenant.id)
      apiKeyId = t |> Option.map (fun x -> x.apiKeyId)
      action   = action
      resource = ctx.request.path
      outcome  = Deny
      remoteIp = None
      details  = Some details }
  try q.auditLog.Append ev with _ -> ()

/// POST /ingest/metrics — accepts a single object, JSON array, or NDJSON.
/// When `quotas` is `Some`, each distinct metric name is admitted against
/// the tenant cardinality cap; points for rejected names are dropped and
/// counted under `"rejectedCardinality"`.
let metrics (store : MetricStore) (hub : Broadcaster)
            (quotas : IngestQuotas option) : WebPart =
  fun ctx -> async {
    try
      let body = readBody ctx
      let items =
        if isNdjson body then parseNdjson body
        else parseRootAsArray body
      let tenantId =
        PulseBoard.Rbac.tryGetTenant ctx
        |> Option.map (fun t -> t.tenant.id)
      let mutable accepted = 0
      let mutable rejected = 0
      let mutable rejectedCap = 0
      for el in items do
        match tryGetString el "name", tryGetDouble el "value" with
        | Some name, Some value ->
          let admit =
            match quotas, tenantId with
            | Some q, Some tid ->
              match q.limiter.TryAdmitSeries(tid, name) with
              | CardinalityResult.Ok -> true
              | CardinalityResult.Rejected cap ->
                rejected    <- rejected + 1
                rejectedCap <- cap
                emitQuotaDeny q ctx "quota.cardinality"
                  (sprintf "series=%s cap=%d" name cap)
                false
            | _ -> true
          if admit then
            let ts = tryGetInt64 el "ts" |> Option.defaultWith nowMs
            let p = { ts = ts; value = value }
            store.Record(name, p)
            publishMetric hub name p
            accepted <- accepted + 1
        | _ -> ()
      let body =
        if rejected > 0 then
          sprintf """{"accepted":%d,"rejectedCardinality":%d,"cap":%d}"""
            accepted rejected rejectedCap
        else
          sprintf """{"accepted":%d}""" accepted
      return! (OK body >=> Writers.setMimeType "application/json") ctx
    with ex ->
      return! BAD_REQUEST (sprintf """{"error":%s}""" (JsonSerializer.Serialize ex.Message)) ctx
  }

/// POST /ingest/logs — accepts a single object, JSON array, or NDJSON.
/// When `quotas` is `Some`, the raw request body length (UTF-8 bytes) is
/// charged against the tenant LogBytes token bucket; over-quota requests
/// are rejected with 429 before any payload parsing.
let logs (store : LogStore) (hub : Broadcaster)
         (quotas : IngestQuotas option) : WebPart =
  fun ctx -> async {
    try
      let body = readBody ctx
      let tenantId =
        PulseBoard.Rbac.tryGetTenant ctx
        |> Option.map (fun t -> t.tenant.id)
      let bytes = float (Encoding.UTF8.GetByteCount body)
      let throttle =
        match quotas, tenantId with
        | Some q, Some tid ->
          match q.limiter.TryAcquire(tid, LogBytes, bytes) with
          | AcquireResult.Ok -> None
          | AcquireResult.Throttled ms ->
            emitQuotaDeny q ctx "quota.logBytes"
              (sprintf "bytes=%g retryAfterMs=%d" bytes ms)
            Some ms
        | _ -> None
      match throttle with
      | Some ms ->
        let retrySec = max 1 (int (ceil (float ms / 1000.0)))
        let body =
          sprintf
            """{"error":"rate limit exceeded","kind":"logBytes","retryAfterMs":%d}""" ms
        return!
          (TOO_MANY_REQUESTS body
           >=> Writers.setMimeType "application/json"
           >=> Writers.setHeader "Retry-After" (string retrySec)) ctx
      | None ->
        let items =
          if isNdjson body then parseNdjson body
          else parseRootAsArray body
        let mutable accepted = 0
        for el in items do
          let ts      = tryGetInt64  el "ts"      |> Option.defaultWith nowMs
          let service = tryGetString el "service" |> Option.defaultValue "unknown"
          let level   = tryGetString el "level"   |> Option.defaultValue "info"
          let message = tryGetString el "message" |> Option.defaultValue ""
          let entry : LogEntry =
            { ts = ts; service = service; level = level; message = message }
          store.Add entry
          publishLog hub entry
          accepted <- accepted + 1
        return! (OK (sprintf """{"accepted":%d}""" accepted)
                 >=> Writers.setMimeType "application/json") ctx
    with ex ->
      return! BAD_REQUEST (sprintf """{"error":%s}""" (JsonSerializer.Serialize ex.Message)) ctx
  }

let webPart (metricStore : MetricStore) (logStore : LogStore)
            (hub : Broadcaster) (quotas : IngestQuotas option) : WebPart =
  choose [
    POST >=> path "/ingest/metrics" >=> metrics metricStore hub quotas
    POST >=> path "/ingest/logs"    >=> logs    logStore    hub quotas
  ]

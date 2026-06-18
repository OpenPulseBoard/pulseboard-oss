module PulseBoard.Ingest

open System
open System.Text
open System.Text.Json
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open Suave.ServerErrors
open PulseBoard.TimeSeries
open PulseBoard.Tenancy
open PulseBoard.Quotas
open PulseBoard.Audit
open PulseBoard.Gateway

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

/// Self-observability sink. When `Some` the ingest handlers record
/// `pulse_ingest_total` / `pulse_ingest_errors_total` /
/// `pulse_quota_deny_total` into the in-process MetricStore so the
/// `__meta__` tenant's curated dashboard can plot them. Optional so
/// single-tenant builds and tests can pass `None`.
let private bumpSelf (selfM : MetricStore option) (name : string) (v : float) =
  match selfM with
  | Some m ->
    try m.Record(name, { ts = nowMs (); value = v }) with _ -> ()
  | None -> ()

let private emitQuotaDeny (q : IngestQuotas) (selfM : MetricStore option)
                          (ctx : HttpContext)
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
  bumpSelf selfM "pulse_quota_deny_total" 1.0

/// POST /ingest/metrics — accepts a single object, JSON array, or NDJSON.
/// When `quotas` is `Some`, each distinct metric name is admitted against
/// the tenant cardinality cap; points for rejected names are dropped and
/// counted under `"rejectedCardinality"`. Accepted points are buffered
/// then handed to `IStorageClient.WriteMetricSamples` in one batch — the
/// in-process client writes straight to MetricStore/Hub; in
/// `--role=edge` it POSTs protobuf to the storage tier.
let metrics (storage : IStorageClient) (quotas : IngestQuotas option)
            (meter : PulseBoard.Billing.IBillingMeter option)
            (costs : PulseBoard.Costs.ICostTracker option)
            (killer : PulseBoard.CardinalityKiller.ICardinalityKillerStore option)
            (selfMetrics : MetricStore option) : WebPart =
  fun ctx -> async {
    PulseBoard.HeartbeatClient.bump ()
    try
      let body = readBody ctx
      let items =
        if isNdjson body then parseNdjson body
        else parseRootAsArray body
      let tenantId =
        PulseBoard.Rbac.tryGetTenant ctx
        |> Option.map (fun t -> t.tenant.id)
      let mutable rejected = 0
      let mutable rejectedCap = 0
      let samples = ResizeArray<MetricSample>()
      for el in items do
        match tryGetString el "name", tryGetDouble el "value" with
        | Some rawName, Some value ->
          // Phase 14.3 — strip any labels the tenant has marked as
          // runaway cardinality before we measure or store anything.
          // Cheap no-op when no kill rules are active for this tenant.
          let name =
            match killer, tenantId with
            | Some k, Some tid -> PulseBoard.CardinalityKiller.stripLabels k tid rawName
            | _ -> rawName
          let admit =
            match quotas, tenantId with
            | Some q, Some tid ->
              match q.limiter.TryAdmitSeries(tid, name) with
              | CardinalityResult.Ok -> true
              | CardinalityResult.Rejected cap ->
                rejected    <- rejected + 1
                rejectedCap <- cap
                emitQuotaDeny q selfMetrics ctx "quota.cardinality"
                  (sprintf "series=%s cap=%d" name cap)
                false
            | _ -> true
          if admit then
            let ts = tryGetInt64 el "ts" |> Option.defaultWith nowMs
            samples.Add { seriesName = name; tsMs = ts; value = value }
        | _ -> ()
      let tid = match tenantId with Some (TenantId s) -> s | None -> ""
      do! storage.WriteMetricSamples(tid, samples)
      let accepted = samples.Count
      printfn
        "[ingest] signal=metrics accepted=%d rejectedCardinality=%d parsedItems=%d tenant=%s bodyBytes=%d"
        accepted rejected items.Length
        (if tid = "" then "<none>" else tid)
        (Encoding.UTF8.GetByteCount body)
      // Self-observability counters — read by the `__meta__` tenant's
      // dashboard. `pulse_ingest_total` counts admitted samples (one
      // record per sample makes the counter sum match the dashboard's
      // "ops over window" intuition); `pulse_ingest_errors_total`
      // covers cardinality rejections at this layer (request-level
      // exceptions are bumped in the `with` arm below).
      if accepted > 0 then
        bumpSelf selfMetrics "pulse_ingest_total" (float accepted)
      if rejected > 0 then
        bumpSelf selfMetrics "pulse_ingest_errors_total" (float rejected)
      // Phase 7 #1 — meter ingest bytes against the tenant's quota even
      // when no rate-limit was applied. We count raw request bytes (not
      // sample count) so the SaaS bill matches the size of the workload.
      match meter, tenantId with
      | Some m, Some tenant when accepted > 0 ->
        let bytes = int64 (Encoding.UTF8.GetByteCount body)
        m.Record (tenant, PulseBoard.Billing.IngestBytes, bytes)
      | _ -> ()
      // Phase 8 #1 — per-series cost attribution. We bucket the request
      // bytes proportionally across the distinct series in the batch so
      // the cardinality explorer can rank "this series is costing $X".
      match costs, tenantId with
      | Some c, Some tenant when accepted > 0 ->
        let totalBytes = int64 (Encoding.UTF8.GetByteCount body)
        let perSample  = if accepted > 0 then totalBytes / int64 accepted else 0L
        // Aggregate by name first so we record one cell per series.
        let groups = System.Collections.Generic.Dictionary<string, int>()
        for s in samples do
          let prev = match groups.TryGetValue s.seriesName with true, v -> v | _ -> 0
          groups.[s.seriesName] <- prev + 1
        for KeyValue(name, n) in groups do
          c.RecordSamples (tenant, name, n, perSample * int64 n)
      | _ -> ()
      let body =
        if rejected > 0 then
          sprintf """{"accepted":%d,"rejectedCardinality":%d,"cap":%d}"""
            accepted rejected rejectedCap
        else
          sprintf """{"accepted":%d}""" accepted
      return! (OK body >=> Writers.setMimeType "application/json") ctx
    with ex ->
      bumpSelf selfMetrics "pulse_ingest_errors_total" 1.0
      return! INTERNAL_ERROR (sprintf """{"error":%s}""" (JsonSerializer.Serialize ex.Message)) ctx
  }

/// POST /ingest/logs — accepts a single object, JSON array, or NDJSON.
/// When `quotas` is `Some`, the raw request body length (UTF-8 bytes) is
/// charged against the tenant LogBytes token bucket; over-quota requests
/// are rejected with 429 before any payload parsing.
let logs (storage : IStorageClient) (quotas : IngestQuotas option)
         (secrets : PulseBoard.Secrets.ISecretsStore option)
         (meter : PulseBoard.Billing.IBillingMeter option)
         (selfMetrics : MetricStore option) : WebPart =
  fun ctx -> async {
    PulseBoard.HeartbeatClient.bump ()
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
            emitQuotaDeny q selfMetrics ctx "quota.logBytes"
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
        let entries = ResizeArray<LogEntry>()
        for el in items do
          let ts      = tryGetInt64  el "ts"      |> Option.defaultWith nowMs
          let service = tryGetString el "service" |> Option.defaultValue "unknown"
          let level   = tryGetString el "level"   |> Option.defaultValue "info"
          let message = tryGetString el "message" |> Option.defaultValue ""
          let message =
            match secrets, tenantId with
            | Some s, Some (TenantId tid) ->
              PulseBoard.Secrets.encryptInlineMarkers s tid message
            | _ -> message
          entries.Add { ts = ts; service = service; level = level; message = message }
        let tid = match tenantId with Some (TenantId s) -> s | None -> ""
        do! storage.WriteLogs(tid, entries)
        if entries.Count > 0 then
          bumpSelf selfMetrics "pulse_ingest_total" (float entries.Count)
        // Phase 7 #1 — meter log bytes for billing.
        match meter, tenantId with
        | Some m, Some tenant when entries.Count > 0 ->
          m.Record (tenant, PulseBoard.Billing.LogBytes, int64 bytes)
        | _ -> ()
        return! (OK (sprintf """{"accepted":%d}""" entries.Count)
                 >=> Writers.setMimeType "application/json") ctx
    with ex ->
      bumpSelf selfMetrics "pulse_ingest_errors_total" 1.0
      return! INTERNAL_ERROR (sprintf """{"error":%s}""" (JsonSerializer.Serialize ex.Message)) ctx
  }

let webPart (storage : IStorageClient) (quotas : IngestQuotas option)
            (secrets : PulseBoard.Secrets.ISecretsStore option)
            (meter   : PulseBoard.Billing.IBillingMeter option)
            (costs   : PulseBoard.Costs.ICostTracker option)
            (killer  : PulseBoard.CardinalityKiller.ICardinalityKillerStore option)
            (selfMetrics : MetricStore option) : WebPart =
  choose [
    POST >=> path "/ingest/metrics" >=> metrics storage quotas meter costs killer selfMetrics
    POST >=> path "/ingest/logs"    >=> logs    storage quotas secrets meter selfMetrics
  ]

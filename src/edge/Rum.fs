module PulseBoard.Rum

open System
open System.Text
open System.Text.Json
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open PulseBoard.Tenancy
open PulseBoard.TimeSeries

// Phase 4 #4 — RUM (Real User Monitoring) stub.
//
// Browsers POST JSON beacons to `/rum/v1/events`. We translate each
// beacon into the same primitives the rest of PulseBoard already
// stores:
//
//   * `web_vital` events  -> a metric named `rum_<name>_<unit>`
//   * `page_load` events  -> the metric `rum_page_load_ms`
//   * `error` events      -> a log line at `level=error, service=rum`
//
// This intentionally avoids inventing a new storage path: every
// downstream that already reads from `MetricStore` / `LogStore`
// (dashboards, alerts, query API, retention) immediately sees RUM
// data without any further plumbing.
//
// AUTH NOTE: this is a *stub*. Beacons originate from the browser and
// cannot safely carry server-side API keys, so the endpoint is
// unauthenticated. In multi-tenant mode the tenant id is taken from
// the URL path (`/rum/v1/<tenantId>/events`) and trusted — a real
// deployment would validate against a published-client-key registry.
// In single-tenant mode it pins to `__local__`.

let singleTenantId = TenantId "__local__"

let private nowMs () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

let private jsonOk (body : string) : WebPart =
  OK body >=> Writers.setMimeType "application/json"

let private errJson (status : int) (msg : string) : WebPart =
  let body = sprintf """{"error":%s}""" (JsonSerializer.Serialize msg)
  let writer =
    match status with
    | 400 -> BAD_REQUEST
    | 413 ->
      // Suave 3.4 doesn't expose a 413 helper, hand-roll one.
      fun body ->
        Response.response HttpCode.HTTP_413
          (Encoding.UTF8.GetBytes(body : string))
    | _   -> Suave.ServerErrors.INTERNAL_ERROR
  writer body >=> Writers.setMimeType "application/json"

let private readBody (req : HttpRequest) : string =
  if isNull req.rawForm || req.rawForm.Length = 0 then ""
  else Encoding.UTF8.GetString req.rawForm

let private tryGetString (el : JsonElement) (name : string) : string option =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.String -> Some (v.GetString())
  | _ -> None

let private tryGetDouble (el : JsonElement) (name : string) : float option =
  match el.TryGetProperty name with
  | true, v ->
    match v.ValueKind with
    | JsonValueKind.Number -> Some (v.GetDouble())
    | JsonValueKind.String ->
      let mutable d = 0.0
      if Double.TryParse(v.GetString(), &d) then Some d else None
    | _ -> None
  | _ -> None

let private tryGetInt64 (el : JsonElement) (name : string) : int64 option =
  match el.TryGetProperty name with
  | true, v ->
    match v.ValueKind with
    | JsonValueKind.Number ->
      let mutable n = 0L
      if v.TryGetInt64 &n then Some n else None
    | _ -> None
  | _ -> None

// Sanitise a metric/series name fragment: lowercase, keep alnum, swap
// other chars for '_'. Prevents callers from polluting the metric
// namespace with arbitrary characters.
let private sanitise (s : string) : string =
  let sb = StringBuilder(s.Length)
  for c in s do
    if Char.IsLetterOrDigit c then sb.Append(Char.ToLower c) |> ignore
    else sb.Append '_' |> ignore
  sb.ToString()

let private classifyVital (name : string) : string * string =
  // (metricSuffix, unit) — CLS is unitless, everything else is ms.
  match name.ToLowerInvariant() with
  | "cls" -> "cls", ""
  | n     -> n, "ms"

let private maxBodyBytes = 64 * 1024

let private ingestEvents
              (metrics : MetricStore)
              (logs    : LogStore)
              (service : string)
              (events  : JsonElement) : int =
  let mutable n = 0
  if events.ValueKind <> JsonValueKind.Array then n
  else
    for ev in events.EnumerateArray() do
      let ts =
        tryGetInt64 ev "ts"
        |> Option.defaultWith nowMs
      let ty =
        tryGetString ev "type"
        |> Option.defaultValue ""
        |> fun s -> s.ToLowerInvariant()
      match ty with
      | "web_vital" | "vital" ->
        match tryGetString ev "name", tryGetDouble ev "value" with
        | Some name, Some value ->
          let suffix, unit = classifyVital name
          let metric =
            if unit = "" then sprintf "rum_%s" suffix
            else sprintf "rum_%s_%s" suffix unit
          metrics.Record(metric, { ts = ts; value = value })
          n <- n + 1
        | _ -> ()
      | "page_load" | "navigation" ->
        match tryGetDouble ev "durationMs" with
        | Some d ->
          metrics.Record("rum_page_load_ms", { ts = ts; value = d })
          n <- n + 1
        | None -> ()
      | "error" | "exception" ->
        let msg =
          tryGetString ev "message"
          |> Option.defaultValue ""
        let stack =
          tryGetString ev "stack"
          |> Option.map (fun s -> "\n" + s)
          |> Option.defaultValue ""
        logs.Add(
          { ts      = ts
            service = sprintf "rum/%s" service
            level   = "error"
            message = msg + stack })
        // Also bump a counter so error rate is alertable from RUM.
        metrics.Record("rum_errors_total", { ts = ts; value = 1.0 })
        n <- n + 1
      | "custom" ->
        match tryGetString ev "name", tryGetDouble ev "value" with
        | Some name, Some value ->
          metrics.Record(sprintf "rum_custom_%s" (sanitise name),
                         { ts = ts; value = value })
          n <- n + 1
        | _ -> ()
      | _ -> ()
    n

/// Build the RUM beacon surface. `multiTenant` toggles path-based
/// tenant routing (`/rum/v1/<tenantId>/events`) vs single-tenant
/// (`/rum/v1/events`, pinned to `__local__`).
let webPart (multiTenant : bool)
            (metrics : MetricStore)
            (logs    : LogStore) : WebPart =

  let handle (tid : TenantId) : WebPart =
    fun ctx -> async {
      let raw = ctx.request.rawForm
      if isNull raw || raw.Length = 0 then
        return! errJson 400 "empty body" ctx
      elif raw.Length > maxBodyBytes then
        return! errJson 413 "beacon too large" ctx
      else
        try
          use doc = JsonDocument.Parse(readBody ctx.request)
          let root = doc.RootElement
          let serviceLabel =
            // The tenant is the trust boundary; service is just a
            // hint for log routing.
            let (TenantId s) = tid
            tryGetString root "service"
            |> Option.defaultValue s
          let events =
            match root.TryGetProperty "events" with
            | true, v -> v
            | _ -> root  // tolerate a bare array body too
          let n = ingestEvents metrics logs serviceLabel events
          let body = sprintf """{"accepted":%d}""" n
          return! jsonOk body ctx
        with
        | :? JsonException as ex ->
          return! errJson 400 ("invalid json: " + ex.Message) ctx
        | ex ->
          return! errJson 500 ex.Message ctx
    }

  // CORS preflight — browsers will OPTIONS the beacon endpoint when
  // sent cross-origin. Reflect the request origin so dev pages work
  // without bespoke config.
  let cors : WebPart =
    Writers.setHeader "Access-Control-Allow-Origin" "*"
    >=> Writers.setHeader "Access-Control-Allow-Methods" "POST, OPTIONS"
    >=> Writers.setHeader "Access-Control-Allow-Headers" "Content-Type"

  let preflightSingle : WebPart =
    OPTIONS >=> path "/rum/v1/events" >=> cors >=> Suave.Successful.NO_CONTENT

  let preflightMulti : WebPart =
    OPTIONS >=> pathScan "/rum/v1/%s/events" (fun _ ->
      cors >=> Suave.Successful.NO_CONTENT)

  if multiTenant then
    choose [
      preflightMulti
      POST >=> pathScan "/rum/v1/%s/events" (fun rawTid ->
        cors >=> handle (TenantId rawTid))
    ]
  else
    choose [
      preflightSingle
      POST >=> path "/rum/v1/events" >=> cors >=> handle singleTenantId
    ]

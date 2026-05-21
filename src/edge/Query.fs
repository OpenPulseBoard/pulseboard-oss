module PulseBoard.Query

open System.Text
open System.Text.Json
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open PulseBoard.TimeSeries
open PulseBoard.Rollups

let private json (s : string) : WebPart =
  OK s >=> Writers.setMimeType "application/json"

let private serializePoints (points : Point array) =
  let sb = StringBuilder()
  sb.Append '[' |> ignore
  for i in 0 .. points.Length - 1 do
    if i > 0 then sb.Append ',' |> ignore
    let p = points.[i]
    sb.AppendFormat(
      System.Globalization.CultureInfo.InvariantCulture,
      "[{0},{1}]", p.ts, p.value) |> ignore
  sb.Append ']' |> ignore
  sb.ToString()

let private serializeLogs (entries : LogEntry array) =
  let arr =
    entries
    |> Array.map (fun e ->
        sprintf """{"ts":%d,"service":%s,"level":%s,"message":%s}"""
          e.ts
          (JsonSerializer.Serialize e.service)
          (JsonSerializer.Serialize e.level)
          (JsonSerializer.Serialize e.message))
  "[" + System.String.Join(",", arr) + "]"

/// GET /api/metrics — list known metric names.
let metricNames (store : MetricStore) : WebPart =
  fun ctx ->
    let names = store.Names()
    let body =
      names
      |> Array.map JsonSerializer.Serialize
      |> fun a -> "[" + System.String.Join(",", a) + "]"
    json body ctx

/// GET /api/metrics/<name>?sinceMs=...&step=<ms|auto|raw>&agg=avg|min|max|sum|count
///
/// Resolution selection (PLAN.md Phase 3 step 4):
///   * `step=<N>` — explicit bucket width in ms. Must match a
///     configured rollup resolution (1m=60000, 5m=300000,
///     1h=3600000). Anything else falls back to raw.
///   * `step=raw` — always serve raw points.
///   * `step=auto` (default) — pick by window length:
///         <1h: raw · <12h: 1m · <7d: 5m · >=7d: 1h.
///   * `agg=<avg|min|max|sum|count>` — only meaningful when a
///     rollup resolution is in play; default is `avg`.
///
/// When `rollupStore` is `None` (rollups disabled) every request
/// falls through to raw points.
let metricSeries (store : MetricStore) (rollupStore : RollupStore option) : WebPart =
  pathScan "/api/metrics/%s" (fun name ->
    fun ctx ->
      let qp k =
        match ctx.request.queryParam k with
        | Choice1Of2 v -> Some v
        | _            -> None
      let sinceMs =
        qp "sinceMs"
        |> Option.bind (fun v ->
            match System.Int64.TryParse v with
            | true, n -> Some n
            | _       -> None)
      let stepRaw = qp "step" |> Option.defaultValue "auto"
      let agg =
        qp "agg"
        |> Option.bind tryParseAgg
        |> Option.defaultValue Agg.Avg
      let chosen : Resolution option =
        match rollupStore with
        | None -> None
        | Some _ ->
          match stepRaw.Trim().ToLowerInvariant() with
          | "raw" -> None
          | "" | "auto" ->
            match sinceMs with
            | None    -> None
            | Some s  -> autoResolution (nowMs () - s)
          | s ->
            match System.Int64.TryParse s with
            | true, n -> tryParseResolutionMs n
            | _       -> None
      let points =
        match chosen, rollupStore, sinceMs with
        | Some res, Some rs, Some s ->
          rs.GetSinceAgg(name, res.Ms, s, agg)
        | Some res, Some rs, None ->
          rs.GetSinceAgg(name, res.Ms, 0L, agg)
        | _, _, Some s -> store.GetSince(name, s)
        | _, _, None   -> store.Get name
      json (serializePoints points) ctx)

/// GET /api/logs?tail=200
let logTail (logs : LogStore) : WebPart =
  fun ctx ->
    let tail =
      match ctx.request.queryParam "tail" with
      | Choice1Of2 v ->
        match System.Int32.TryParse v with
        | true, n when n > 0 -> n
        | _ -> 200
      | _ -> 200
    json (serializeLogs (logs.Tail tail)) ctx

let webPart (metricStore : MetricStore) (logStore : LogStore)
            (rollupStore : RollupStore option) : WebPart =
  choose [
    GET >=> path "/api/metrics"        >=> metricNames metricStore
    GET >=> metricSeries metricStore rollupStore
    GET >=> path "/api/logs"           >=> logTail logStore
  ]

module PulseBoard.Query

open System.Text
open System.Text.Json
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open PulseBoard.TimeSeries

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

/// GET /api/metrics/<name>?sinceMs=...
let metricSeries (store : MetricStore) : WebPart =
  pathScan "/api/metrics/%s" (fun name ->
    fun ctx ->
      let sinceMs =
        match ctx.request.queryParam "sinceMs" with
        | Choice1Of2 v ->
          match System.Int64.TryParse v with
          | true, n -> Some n
          | _ -> None
        | _ -> None
      let points =
        match sinceMs with
        | Some s -> store.GetSince(name, s)
        | None   -> store.Get name
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

let webPart (metricStore : MetricStore) (logStore : LogStore) : WebPart =
  choose [
    GET >=> path "/api/metrics"        >=> metricNames metricStore
    GET >=> metricSeries metricStore
    GET >=> path "/api/logs"           >=> logTail logStore
  ]

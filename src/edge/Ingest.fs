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

/// POST /ingest/metrics — accepts a single object, JSON array, or NDJSON.
let metrics (store : MetricStore) (hub : Broadcaster) : WebPart =
  fun ctx -> async {
    try
      let body = readBody ctx
      let items =
        if isNdjson body then parseNdjson body
        else parseRootAsArray body
      let mutable accepted = 0
      for el in items do
        match tryGetString el "name", tryGetDouble el "value" with
        | Some name, Some value ->
          let ts = tryGetInt64 el "ts" |> Option.defaultWith nowMs
          let p = { ts = ts; value = value }
          store.Record(name, p)
          publishMetric hub name p
          accepted <- accepted + 1
        | _ -> ()
      return! (OK (sprintf """{"accepted":%d}""" accepted)
               >=> Writers.setMimeType "application/json") ctx
    with ex ->
      return! BAD_REQUEST (sprintf """{"error":%s}""" (JsonSerializer.Serialize ex.Message)) ctx
  }

/// POST /ingest/logs — accepts a single object, JSON array, or NDJSON.
let logs (store : LogStore) (hub : Broadcaster) : WebPart =
  fun ctx -> async {
    try
      let body = readBody ctx
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

let webPart (metricStore : MetricStore) (logStore : LogStore) (hub : Broadcaster) : WebPart =
  choose [
    POST >=> path "/ingest/metrics" >=> metrics metricStore hub
    POST >=> path "/ingest/logs"    >=> logs    logStore    hub
  ]

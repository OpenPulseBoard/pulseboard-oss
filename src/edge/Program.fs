module PulseBoard.Program

open System
open System.IO
open System.Net
open System.Threading
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open Suave.WebSocket
open PulseBoard.TimeSeries
open PulseBoard.Hub
open PulseBoard.Alerts

/// Locate the wwwroot folder regardless of where the binary is invoked from.
let private resolveWwwRoot () =
  let candidates =
    [ Path.Combine(AppContext.BaseDirectory, "wwwroot")
      Path.Combine(Directory.GetCurrentDirectory(), "wwwroot")
      Path.Combine(Directory.GetCurrentDirectory(), "examples", "PulseBoard", "wwwroot") ]
  candidates
  |> List.tryFind Directory.Exists
  |> Option.defaultValue (Path.Combine(AppContext.BaseDirectory, "wwwroot"))

[<EntryPoint>]
let main argv =
  let port =
    match argv |> Array.tryFind (fun a -> a.StartsWith "--port=") with
    | Some s ->
      match Int32.TryParse(s.Substring(7)) with
      | true, n -> n
      | _ -> 8080
    | None -> 8080

  let dataDir =
    match argv |> Array.tryFind (fun a -> a.StartsWith "--data=") with
    | Some s -> s.Substring 7
    | None   -> Path.Combine(Directory.GetCurrentDirectory(), "pulse-data")

  // Per-token auth for /ingest/*. Tokens are loaded from --tokens-file=<path>
  // (one `name:secret` per line, # comments OK) or from the PULSE_TOKENS env
  // var (comma- or newline-separated). When empty, ingest is left OPEN so the
  // demo "just works" — a loud warning is printed in that case.
  let tokens =
    match argv |> Array.tryFind (fun a -> a.StartsWith "--tokens-file=") with
    | Some s -> PulseBoard.Auth.loadFromFile (s.Substring 14)
    | None   -> PulseBoard.Auth.loadFromEnv  "PULSE_TOKENS"

  // Outbound alert delivery. `--webhook=` / `--slack=` may be repeated on
  // the command line; `PULSE_WEBHOOKS` / `PULSE_SLACK` env vars accept a
  // comma/newline-separated list. Each endpoint becomes its own sink so a
  // slow or failing receiver can't block the others.
  let argUrls (prefix : string) =
    argv
    |> Array.choose (fun a ->
        if a.StartsWith prefix then Some (a.Substring prefix.Length) else None)
    |> Array.toList
  let envUrls (name : string) =
    PulseBoard.Notify.parseUrls (Environment.GetEnvironmentVariable name)
  let webhookUrls = argUrls "--webhook=" @ envUrls "PULSE_WEBHOOKS"
  let slackUrls   = argUrls "--slack="   @ envUrls "PULSE_SLACK"

  let metricStore = MetricStore(capacityPerMetric = 4096)
  let logStore    = LogStore(capacity = 4096)
  let hub         = Broadcaster()

  // On-disk segment store: 1 MiB per segment (~65k points per file).
  let segments = new PulseBoard.Segments.SegmentStore(dataDir)
  metricStore.SetOnAppend   segments.Append
  metricStore.SetHistory    segments.ReadSince
  metricStore.SetExtraNames segments.KnownNames

  printfn "PulseBoard persisting metric history under %s" dataDir

  // Demo alert rule: cpu > 0.9 sustained for 30s.
  let consoleSink : PulseBoard.Notify.Sink =
    fun alert ->
      printfn "[ALERT] %s metric=%s value=%f at=%d"
        alert.rule alert.metric alert.value alert.firedAt

  let hubSink : PulseBoard.Notify.Sink =
    fun alert ->
      let payload =
        sprintf """{"type":"alert","rule":%s,"metric":%s,"value":%f,"firedAt":%d}"""
          (System.Text.Json.JsonSerializer.Serialize alert.rule)
          (System.Text.Json.JsonSerializer.Serialize alert.metric)
          alert.value alert.firedAt
      hub.Publish payload

  let alertSink =
    PulseBoard.Notify.fanout (
      [ consoleSink; hubSink ]
      @ (webhookUrls |> List.map PulseBoard.Notify.webhook)
      @ (slackUrls   |> List.map PulseBoard.Notify.slack))

  let alertEngine = Engine(metricStore, alertSink)

  alertEngine.Add
    { name = "cpu-high"; metric = "cpu"; cmp = Gt
      threshold = 0.9; durationMs = 30_000L }

  // Background timer to evaluate rules every 2s.
  let evalTimer =
    new Timer((fun _ -> try alertEngine.Tick() with _ -> ()),
              null, TimeSpan.FromSeconds 2., TimeSpan.FromSeconds 2.)

  // Flush segment writers every second so readers (and crash recovery)
  // observe data without a clean shutdown.
  let flushTimer =
    new Timer((fun _ -> try segments.Flush() with _ -> ()),
              null, TimeSpan.FromSeconds 1., TimeSpan.FromSeconds 1.)

  // Flush segment writers on graceful shutdown (Ctrl+C / SIGTERM).
  let flushAndDispose () =
    try segments.Flush() with _ -> ()
    try (segments :> IDisposable).Dispose() with _ -> ()
  AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> flushAndDispose ())
  Console.CancelKeyPress.Add(fun _ -> flushAndDispose ())

  let wwwroot = resolveWwwRoot ()
  printfn "PulseBoard serving static files from %s" wwwroot

  let ingest =
    // Gate auth on the path itself: when the request isn't aimed at /ingest/*
    // we let `choose` move on to the next arm (query/static/etc.) instead of
    // letting Basic-Auth's 401 challenge short-circuit unrelated routes.
    pathStarts "/ingest" >=>
      PulseBoard.Auth.protect tokens
        (PulseBoard.Ingest.webPart metricStore logStore hub)

  let app : WebPart =
    choose [
      ingest
      PulseBoard.Query.webPart  metricStore logStore
      path "/ws"   >=> handShake (Hub.handler hub)
      GET >=> path "/"      >=> Files.browseFile wwwroot "index.html"
      GET >=> path "/index.html" >=> Files.browseFile wwwroot "index.html"
      GET >=> Files.browse wwwroot
      NOT_FOUND "Not found."
    ]

  let config =
    { defaultConfig with
        bindings   = [ HttpBinding.create HTTP IPAddress.Loopback (uint16 port) ]
        homeFolder = Some wwwroot }

  printfn "PulseBoard listening on http://127.0.0.1:%d" port
  if Map.isEmpty tokens then
    printfn "  [WARN] /ingest/* is OPEN. Provide --tokens-file=<path> or PULSE_TOKENS to require auth."
  else
    printfn "  Auth: %d token(s) loaded; /ingest/* requires HTTP Basic." tokens.Count
  match webhookUrls, slackUrls with
  | [], [] ->
    printfn "  Alert delivery: console + WebSocket hub only (use --webhook=URL / --slack=URL to fan out)."
  | _ ->
    printfn "  Alert delivery: console + WebSocket hub + %d webhook(s) + %d Slack endpoint(s)."
      webhookUrls.Length slackUrls.Length
  printfn "  POST /ingest/metrics   (JSON or JSON array)"
  printfn "  POST /ingest/logs      (JSON, array, or NDJSON)"
  printfn "  GET  /api/metrics      (list)"
  printfn "  GET  /api/metrics/<n>?sinceMs=...   (series)"
  printfn "  GET  /api/logs?tail=N"
  printfn "  WS   /ws               (live feed)"
  printfn "  GET  /                 (dashboard)"

  startWebServer config app
  GC.KeepAlive evalTimer
  GC.KeepAlive flushTimer
  0

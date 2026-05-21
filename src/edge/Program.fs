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

  // Per-token Basic-Auth for /ingest/* in the legacy / single-tenant mode.
  // Tokens are loaded from --tokens-file=<path> (one `name:secret` per
  // line, # comments OK) or from the PULSE_TOKENS env var (comma- or
  // newline-separated). When empty, ingest is left OPEN so the demo "just
  // works" — a loud warning is printed in that case.
  let tokens =
    match argv |> Array.tryFind (fun a -> a.StartsWith "--tokens-file=") with
    | Some s -> PulseBoard.Auth.loadFromFile (s.Substring 14)
    | None   -> PulseBoard.Auth.loadFromEnv  "PULSE_TOKENS"

  // Phase 1 multi-tenant mode. Off by default so the OSS demo keeps booting
  // with zero config. When enabled, /ingest/* and /api/* both require a
  // scoped API key; legacy Basic-Auth `tokens` are ignored. Tenants live in
  // an in-memory store for this slice — Npgsql-backed `ITenantStore` lands
  // later behind the same interface.
  let multiTenant = argv |> Array.contains "--multi-tenant"
  let seedTenantSlug =
    argv
    |> Array.tryFind (fun a -> a.StartsWith "--seed-tenant=")
    |> Option.map   (fun a -> a.Substring 14)

  let tenantStore : PulseBoard.Tenancy.ITenantStore =
    PulseBoard.Tenancy.InMemoryTenantStore() :> _
  let auditLog : PulseBoard.Audit.IAuditLog =
    PulseBoard.Audit.InMemoryAuditLog(1024) :> _

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

  // Seed a tenant + admin API key when running in multi-tenant mode. Without
  // a seed the in-memory store is empty and every authenticated route 403s,
  // which is correct but unhelpful; print a clear warning instead.
  if multiTenant then
    match seedTenantSlug with
    | Some slug ->
      let t = tenantStore.CreateTenant slug
      let allScopes =
        PulseBoard.Tenancy.Scope.Ingest
        ||| PulseBoard.Tenancy.Scope.Query
        ||| PulseBoard.Tenancy.Scope.Admin
      let issued =
        tenantStore.IssueApiKey(
          t.id, "seed", PulseBoard.Tenancy.Admin, allScopes)
      printfn "  Seeded tenant '%s' with admin API key:" slug
      printfn "    %s" issued.plaintext
      printfn "    (shown once — pass via 'Authorization: Bearer <key>' or 'X-API-Key: <key>')"
    | None ->
      printfn "  [WARN] --multi-tenant set without --seed-tenant=<slug>. No tenants exist; all gated routes will 403."

  // -- Route composition ------------------------------------------------------

  let ingestInner =
    PulseBoard.Ingest.webPart metricStore logStore hub
  let queryInner =
    PulseBoard.Query.webPart  metricStore logStore

  /// GET /api/admin/audit?tail=N — most recent audit events, newest last.
  /// Capped at 1000 per request; default 100. Multi-tenant only.
  let adminAudit : WebPart =
    GET >=> path "/api/admin/audit" >=> (fun ctx -> async {
      let tail =
        match ctx.request.queryParam "tail" with
        | Choice1Of2 v ->
          match Int32.TryParse v with
          | true, n when n > 0 -> min n 1000
          | _ -> 100
        | _ -> 100
      let events = auditLog.Tail tail
      let body =
        events
        |> Array.map PulseBoard.Audit.serialize
        |> String.concat ","
        |> sprintf "[%s]"
      return! (OK body >=> Writers.setMimeType "application/json") ctx
    })

  let ingest =
    pathStarts "/ingest" >=>
      (if multiTenant then
         PulseBoard.Auth.resolveApiKey tenantStore
           (PulseBoard.Rbac.requireScope auditLog
              "ingest" PulseBoard.Tenancy.Scope.Ingest ingestInner)
       else
         PulseBoard.Auth.protect tokens ingestInner)

  let admin : WebPart =
    if multiTenant then
      pathStarts "/api/admin/" >=>
        PulseBoard.Auth.resolveApiKey tenantStore
          (PulseBoard.Rbac.requireScope auditLog
             "admin" PulseBoard.Tenancy.Scope.Admin adminAudit)
    else
      // No admin surface in single-tenant mode — fall through to NOT_FOUND.
      fun _ -> async { return None }

  let query : WebPart =
    if multiTenant then
      pathStarts "/api/" >=>
        PulseBoard.Auth.resolveApiKey tenantStore
          (PulseBoard.Rbac.requireScope auditLog
             "query" PulseBoard.Tenancy.Scope.Query queryInner)
    else
      queryInner

  let app : WebPart =
    choose [
      ingest
      admin     // must precede `query` because /api/admin/* also matches /api/
      query
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
  if multiTenant then
    printfn "  Mode: multi-tenant. /ingest/* requires scope=ingest, /api/* requires scope=query, /api/admin/* requires scope=admin."
  elif Map.isEmpty tokens then
    printfn "  Mode: single-tenant. [WARN] /ingest/* is OPEN. Provide --tokens-file=<path> or PULSE_TOKENS to require auth, or --multi-tenant to switch to scoped API keys."
  else
    printfn "  Mode: single-tenant. Auth: %d token(s) loaded; /ingest/* requires HTTP Basic." tokens.Count
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
  if multiTenant then
    printfn "  GET  /api/admin/audit?tail=N        (Admin scope)"
  printfn "  WS   /ws               (live feed)"
  printfn "  GET  /                 (dashboard)"

  startWebServer config app
  GC.KeepAlive evalTimer
  GC.KeepAlive flushTimer
  0

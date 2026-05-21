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

  // Postgres connection string (used by tenant store + quota overrides).
  let pgConn =
    match argv |> Array.tryFind (fun a -> a.StartsWith "--postgres=") with
    | Some s -> Some (s.Substring 11)
    | None ->
      let v = Environment.GetEnvironmentVariable "PULSE_POSTGRES"
      if String.IsNullOrWhiteSpace v then None else Some v

  let tenantStore : PulseBoard.Tenancy.ITenantStore =
    // Postgres-backed store when --postgres=<connstr> (or PULSE_POSTGRES) is
    // provided; otherwise the in-memory store (data vaporizes on restart).
    // Schema is applied idempotently at startup.
    match pgConn with
    | Some cs ->
      try
        PulseBoard.PgTenantStore.ensureSchema cs
        printfn "  TenantStore: Postgres (schema ensured)"
        PulseBoard.PgTenantStore.PgTenantStore(cs) :> _
      with ex ->
        eprintfn "  [ERROR] failed to initialise Postgres tenant store: %s" ex.Message
        exit 2
    | None ->
      if multiTenant then
        printfn "  TenantStore: in-memory (ephemeral — pass --postgres=... to persist)"
      PulseBoard.Tenancy.InMemoryTenantStore() :> _
  let auditLog : PulseBoard.Audit.IAuditLog =
    // In-memory ring is always present so `GET /api/admin/audit` can serve
    // recent tail; when Postgres is configured we fan out to a durable
    // sink as well (PLAN.md Phase 1 step 4). The ring is listed first so
    // `Tail` reads from it.
    let ring = PulseBoard.Audit.InMemoryAuditLog(1024) :> PulseBoard.Audit.IAuditLog
    match pgConn with
    | Some cs ->
      try
        PulseBoard.PgAuditLog.ensureSchema cs
        printfn "  AuditLog:    Postgres + in-memory ring (durable; tail served from ring)"
        let pg = PulseBoard.PgAuditLog.PgAuditLog(cs) :> PulseBoard.Audit.IAuditLog
        PulseBoard.Audit.CompositeAuditLog([| ring; pg |]) :> _
      with ex ->
        eprintfn "  [WARN] failed to initialise Postgres audit log (%s); ring only"
          ex.Message
        ring
    | None -> ring

  // -- Per-tenant quotas (token bucket; PLAN.md Phase 1 step 5) -------------
  // Defaults are generous enough that the demo and smoke tests never trip
  // them; set capacity to 0 (via --quota-*-burst=0) to disable a kind.
  let parseFloat (envName : string) (flag : string) (fallback : float) =
    let raw =
      match argv |> Array.tryFind (fun a -> a.StartsWith flag) with
      | Some s -> Some (s.Substring flag.Length)
      | None ->
        let v = Environment.GetEnvironmentVariable envName
        if String.IsNullOrWhiteSpace v then None else Some v
    match raw with
    | None -> fallback
    | Some s ->
      match Double.TryParse(s, Globalization.NumberStyles.Float,
                            Globalization.CultureInfo.InvariantCulture) with
      | true, n when n >= 0.0 -> n
      | _ ->
        eprintfn "  [ERROR] %s expects a non-negative number, got %s" flag s
        exit 2
  let ingestRps   = parseFloat "PULSE_QUOTA_INGEST_RPS"   "--quota-ingest-rps="   500.0
  let ingestBurst = parseFloat "PULSE_QUOTA_INGEST_BURST" "--quota-ingest-burst=" 1000.0
  let queryRps    = parseFloat "PULSE_QUOTA_QUERY_RPS"    "--quota-query-rps="    100.0
  let queryBurst  = parseFloat "PULSE_QUOTA_QUERY_BURST"  "--quota-query-burst="  200.0
  let alertRps    = parseFloat "PULSE_QUOTA_ALERT_EVAL_RPS"   "--quota-alert-eval-rps="   0.0
  let alertBurst  = parseFloat "PULSE_QUOTA_ALERT_EVAL_BURST" "--quota-alert-eval-burst=" 0.0
  // Log volume is charged in bytes (one token per UTF-8 byte). A 1 GiB/day
  // budget is ~12,427 B/s sustained; configure via --quota-log-bytes-per-sec.
  let logBps      = parseFloat "PULSE_QUOTA_LOG_BPS"         "--quota-log-bytes-per-sec=" 0.0
  let logBurst    = parseFloat "PULSE_QUOTA_LOG_BURST_BYTES" "--quota-log-burst-bytes="   0.0
  let cardinalityCap =
    let raw =
      match argv |> Array.tryFind (fun a -> a.StartsWith "--quota-cardinality=") with
      | Some s -> Some (s.Substring "--quota-cardinality=".Length)
      | None ->
        let v = Environment.GetEnvironmentVariable "PULSE_QUOTA_CARDINALITY"
        if String.IsNullOrWhiteSpace v then None else Some v
    match raw with
    | None -> 0
    | Some s ->
      match Int32.TryParse s with
      | true, n when n >= 0 -> n
      | _ ->
        eprintfn "  [ERROR] --quota-cardinality expects a non-negative integer, got %s" s
        exit 2
  let quotaDefaults : Map<PulseBoard.Quotas.Kind, PulseBoard.Quotas.Limit> =
    Map.ofList
      [ PulseBoard.Quotas.Ingest,    { capacity = ingestBurst; refillPerSec = ingestRps }
        PulseBoard.Quotas.Query,     { capacity = queryBurst;  refillPerSec = queryRps  }
        PulseBoard.Quotas.AlertEval, { capacity = alertBurst;  refillPerSec = alertRps  }
        PulseBoard.Quotas.LogBytes,  { capacity = logBurst;    refillPerSec = logBps    } ]
  let overrideRepo : PulseBoard.Quotas.IOverrideRepo =
    match pgConn with
    | Some cs ->
      try
        PulseBoard.PgQuotaOverrides.ensureSchema cs
        PulseBoard.PgQuotaOverrides.PgOverrideRepo(cs) :> _
      with ex ->
        eprintfn "  [ERROR] failed to initialise Postgres quota overrides: %s" ex.Message
        exit 2
    | None -> PulseBoard.Quotas.InMemoryOverrideRepo() :> _
  let quotaStore =
    PulseBoard.Quotas.QuotaStore(quotaDefaults, cardinalityCap, overrideRepo)
  let limiter = PulseBoard.Quotas.Limiter(quotaStore)

  // -- OIDC browser SSO ------------------------------------------------------
  // Opt-in: enabled when --oidc-issuer + --oidc-client-id + --oidc-redirect-uri
  // are all present. Requires --multi-tenant (the SSO user maps to a tenant
  // role/scope; single-tenant mode has no concept of tenants to map into).
  let argValue (prefix : string) =
    argv
    |> Array.tryFind (fun a -> a.StartsWith prefix)
    |> Option.map (fun a -> a.Substring prefix.Length)
  let envOr (envName : string) (cli : string option) =
    match cli with
    | Some v -> Some v
    | None ->
      let v = Environment.GetEnvironmentVariable envName
      if String.IsNullOrWhiteSpace v then None else Some v
  let oidcIssuer   = envOr "PULSE_OIDC_ISSUER"        (argValue "--oidc-issuer=")
  let oidcClientId = envOr "PULSE_OIDC_CLIENT_ID"     (argValue "--oidc-client-id=")
  let oidcClientSec= envOr "PULSE_OIDC_CLIENT_SECRET" (argValue "--oidc-client-secret=")
  let oidcRedirect = envOr "PULSE_OIDC_REDIRECT_URI"  (argValue "--oidc-redirect-uri=")
  let oidcTenant   = envOr "PULSE_OIDC_TENANT"        (argValue "--oidc-tenant=")
  let oidcScopes   =
    envOr "PULSE_OIDC_SCOPES" (argValue "--oidc-scopes=")
    |> Option.defaultValue PulseBoard.Oidc.scopesDefault
  let parseRoleFlag (s : string) =
    match s.Trim().ToLowerInvariant() with
    | "viewer"  -> Some PulseBoard.Tenancy.Viewer
    | "editor"  -> Some PulseBoard.Tenancy.Editor
    | "admin"   -> Some PulseBoard.Tenancy.Admin
    | "billing" -> Some PulseBoard.Tenancy.Billing
    | "none" | "deny" | "" -> None
    | other ->
      eprintfn "  [ERROR] unknown role '%s' (allowed: viewer|editor|admin|billing|none)" other
      exit 2
  let oidcDefaultRole =
    match envOr "PULSE_OIDC_DEFAULT_ROLE" (argValue "--oidc-default-role=") with
    | Some s -> parseRoleFlag s
    | None   -> None     // deny new users by default — fail-closed
  let parseEmails (raw : string option) =
    match raw with
    | None -> []
    | Some s ->
      s.Split([| ','; ' '; '\n'; '\r'; '\t' |], StringSplitOptions.RemoveEmptyEntries)
      |> Array.map (fun e -> e.Trim().ToLowerInvariant())
      |> Array.filter (fun e -> e.Length > 0)
      |> Array.toList
  let oidcRoleMap =
    [ PulseBoard.Tenancy.Admin,   envOr "PULSE_OIDC_ADMINS"   (argValue "--oidc-admins=")
      PulseBoard.Tenancy.Editor,  envOr "PULSE_OIDC_EDITORS"  (argValue "--oidc-editors=")
      PulseBoard.Tenancy.Viewer,  envOr "PULSE_OIDC_VIEWERS"  (argValue "--oidc-viewers=")
      PulseBoard.Tenancy.Billing, envOr "PULSE_OIDC_BILLING"  (argValue "--oidc-billing=") ]
    |> List.collect (fun (role, raw) ->
         parseEmails raw |> List.map (fun e -> e, role))
    // Later entries win on duplicate emails; reverse so first-listed flag wins.
    |> List.rev
    |> Map.ofList
  let sessionKey =
    match envOr "PULSE_SESSION_SECRET" (argValue "--session-secret=") with
    | Some s ->
      try PulseBoard.Session.keyFromBase64 s
      with ex ->
        eprintfn "  [ERROR] invalid --session-secret: %s" ex.Message
        exit 2
    | None -> PulseBoard.Session.generateKey ()

  let oidcConfig : PulseBoard.Oidc.Config option =
    match oidcIssuer, oidcClientId, oidcRedirect, oidcTenant with
    | Some iss, Some cid, Some redir, Some slug ->
      Some
        { issuer        = iss
          clientId      = cid
          clientSecret  = oidcClientSec
          redirectUri   = redir
          tenantSlug    = slug.Trim().ToLowerInvariant()
          scopes        = oidcScopes
          cookieSecure  = redir.StartsWith "https://"
          sessionTtl    = PulseBoard.Session.defaultLifetime
          sessionKey    = sessionKey
          defaultRole   = oidcDefaultRole
          roleOverrides = oidcRoleMap }
    | _ -> None

  if oidcConfig.IsSome && not multiTenant then
    eprintfn "  [ERROR] OIDC requires --multi-tenant"
    exit 2

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

  // -- Edge / storage split (PLAN.md Phase 2 step 6) -----------------------
  // Three roles, default `all` (monolith — today's behaviour):
  //   * `all`     : runs every component in one process; receivers write
  //                 through `InProcessStorageClient`. Optionally also
  //                 hosts the internal protocol endpoints when
  //                 `--edge-secret` is supplied, so a separate edge
  //                 process can push into the same store.
  //   * `storage` : same as `all` but expects to be paired with at least
  //                 one edge process; requires `--edge-secret`.
  //   * `edge`    : routes every receiver through `HttpStorageClient`
  //                 to a remote storage tier; requires
  //                 `--storage-endpoint` and `--edge-secret`. Note: in
  //                 this iteration the edge process still allocates the
  //                 in-process MetricStore / hub / alert engine — they
  //                 sit idle (no receiver writes into them) and the
  //                 dashboard / query API run against an empty store. A
  //                 follow-up commit will skip those components in
  //                 `--role=edge` for a true zero-overhead edge.
  let role =
    (argValue "--role=" |> Option.defaultValue "all").ToLowerInvariant()
  if not (List.contains role [ "all"; "edge"; "storage" ]) then
    eprintfn "  [ERROR] --role must be one of: all | edge | storage (got %s)" role
    exit 2
  let storageEndpoint = envOr "PULSE_STORAGE_ENDPOINT" (argValue "--storage-endpoint=")
  let edgeSecretHex   = envOr "PULSE_EDGE_SECRET"      (argValue "--edge-secret=")
  let edgeSecret : byte[] option =
    edgeSecretHex
    |> Option.map (fun s ->
        try PulseBoard.Gateway.secretFromHex s
        with _ ->
          eprintfn "  [ERROR] --edge-secret must be hex-encoded"
          exit 2)
  if role = "edge" && storageEndpoint.IsNone then
    eprintfn "  [ERROR] --role=edge requires --storage-endpoint=URL"
    exit 2
  if (role = "edge" || role = "storage") && edgeSecret.IsNone then
    eprintfn "  [ERROR] --role=%s requires --edge-secret=<hex>" role
    exit 2

  let metricStore = MetricStore(capacityPerMetric = 4096)
  let logStore    = LogStore(capacity = 4096)
  let hub         = Broadcaster()

  // Pluggable storage backends (PLAN.md Phase 3). The receiver-facing
  // seam is still `IStorageClient`; `InProcessStorageClient` now
  // delegates to these. With `--mimir-url=`, `--loki-url=`, or
  // `--tempo-url=` set, the matching pillar fans out over HTTP to an
  // upstream tenant-aware backend (Mimir / Loki / Tempo or a
  // compatible vendor like Grafana Cloud, AWS Managed Prometheus,
  // Honeycomb's OTel proxy, etc.). Without those flags everything
  // stays embedded so the OSS demo keeps booting with zero config.
  //
  // Cardinality enforcement is wired here: when running multi-tenant
  // we pass the `Limiter` into the embedded metric backend; it calls
  // `TryAdmitSeries` per sample and drops samples that would exceed
  // the per-tenant cap. The Mimir backend doesn't enforce locally
  // (Mimir has its own per-tenant series budgets); receivers still
  // do the up-front admission check based on PulseBoard's limiter.
  let mimirUrl   = envOr "PULSE_MIMIR_URL"   (argValue "--mimir-url=")
  let mimirOrg   = envOr "PULSE_MIMIR_ORG_HEADER" (argValue "--mimir-org-header=")
                   |> Option.defaultValue "X-Scope-OrgID"
  let mimirToken = envOr "PULSE_MIMIR_BEARER" (argValue "--mimir-bearer=")
  let lokiUrl    = envOr "PULSE_LOKI_URL"    (argValue "--loki-url=")
  let lokiOrg    = envOr "PULSE_LOKI_ORG_HEADER"  (argValue "--loki-org-header=")
                   |> Option.defaultValue "X-Scope-OrgID"
  let lokiToken  = envOr "PULSE_LOKI_BEARER"  (argValue "--loki-bearer=")
  let tempoUrl   = envOr "PULSE_TEMPO_URL"   (argValue "--tempo-url=")
  let tempoOrg   = envOr "PULSE_TEMPO_ORG_HEADER" (argValue "--tempo-org-header=")
                   |> Option.defaultValue "X-Scope-OrgID"
  let tempoToken = envOr "PULSE_TEMPO_BEARER" (argValue "--tempo-bearer=")
  let optOrg (h : string) =
    if h = "none" || String.IsNullOrWhiteSpace h then None else Some h
  let metricBackend : PulseBoard.Storage.IMetricBackend =
    match mimirUrl with
    | Some url ->
      printfn "  MetricBackend: Mimir remote_write -> %s/api/v1/push (org-header=%s)"
        url mimirOrg
      let opts =
        { PulseBoard.CloudBackends.MimirOptions.Default url with
            OrgIdHeader = optOrg mimirOrg
            Bearer      = mimirToken }
      new PulseBoard.CloudBackends.MimirMetricBackend(opts) :> _
    | None ->
      PulseBoard.Storage.EmbeddedMetricBackend(
        metricStore,
        (if multiTenant then Some limiter else None)) :> _
  let logBackend : PulseBoard.Storage.ILogBackend =
    match lokiUrl with
    | Some url ->
      printfn "  LogBackend:    Loki push -> %s/loki/api/v1/push (org-header=%s)"
        url lokiOrg
      let opts =
        { PulseBoard.CloudBackends.LokiOptions.Default url with
            OrgIdHeader = optOrg lokiOrg
            Bearer      = lokiToken }
      new PulseBoard.CloudBackends.LokiLogBackend(opts) :> _
    | None ->
      PulseBoard.Storage.EmbeddedLogBackend(logStore) :> _
  let traceBackend : PulseBoard.Storage.ITraceBackend =
    match tempoUrl with
    | Some url ->
      printfn "  TraceBackend:  Tempo OTLP/HTTP -> %s/v1/traces (org-header=%s)"
        url tempoOrg
      let opts =
        { PulseBoard.CloudBackends.TempoOptions.Default url with
            OrgIdHeader = optOrg tempoOrg
            Bearer      = tempoToken }
      new PulseBoard.CloudBackends.TempoTraceBackend(opts) :> _
    | None ->
      PulseBoard.Storage.EmbeddedTraceBackend() :> _
  let rawTraceBackend : PulseBoard.CloudBackends.IRawTraceBackend option =
    match traceBackend with
    | :? PulseBoard.CloudBackends.IRawTraceBackend as rt -> Some rt
    | _ -> None

  // -- Retention (PLAN.md Phase 3 step 3) -------------------------------
  // System defaults are configurable per pillar; `0` or unset means
  // "keep forever / no enforcement" (the same convention as the
  // existing quota flags). Per-tenant overrides live in Postgres when
  // available, otherwise in-memory. The embedded compactor walks the
  // in-process MetricStore / LogStore on a timer and prunes anything
  // older than the most-generous configured horizon. When a pillar
  // has been swapped to a cloud backend its TTL is honoured by the
  // upstream service (Mimir / Loki / Tempo) and the compactor skips it.
  let parseTtlMs (envName : string) (flag : string) : int64 option =
    let raw =
      match argv |> Array.tryFind (fun a -> a.StartsWith flag) with
      | Some s -> Some (s.Substring flag.Length)
      | None ->
        let v = Environment.GetEnvironmentVariable envName
        if String.IsNullOrWhiteSpace v then None else Some v
    match raw with
    | None -> None
    | Some s ->
      match Int64.TryParse s with
      | true, n when n > 0L  -> Some n
      | true, _              -> None     // 0 / negative = keep forever
      | _ ->
        eprintfn "  [ERROR] %s expects a non-negative integer (ms), got %s" flag s
        exit 2
  let retentionDefaults : PulseBoard.Retention.RetentionPolicy =
    { metricsMs = parseTtlMs "PULSE_RETENTION_METRICS_MS" "--retention-metrics-ms="
      logsMs    = parseTtlMs "PULSE_RETENTION_LOGS_MS"    "--retention-logs-ms="
      tracesMs  = parseTtlMs "PULSE_RETENTION_TRACES_MS"  "--retention-traces-ms=" }
  let retentionInterval =
    parseTtlMs "PULSE_RETENTION_COMPACT_INTERVAL_MS"
               "--retention-compact-interval-ms="
    |> Option.map int
    |> Option.defaultValue 60_000
  let retentionRepo : PulseBoard.Retention.IRetentionRepo =
    match pgConn with
    | Some cs ->
      try
        PulseBoard.PgRetentionOverrides.ensureSchema cs
        PulseBoard.PgRetentionOverrides.PgRetentionRepo(cs) :> _
      with ex ->
        eprintfn "  [ERROR] failed to initialise Postgres retention overrides: %s" ex.Message
        exit 2
    | None -> PulseBoard.Retention.InMemoryRetentionRepo() :> _
  let retentionStore =
    PulseBoard.Retention.RetentionStore(retentionDefaults, retentionRepo)
  let describeTtl = function
    | Some (n : int64) -> sprintf "%dms" n
    | None             -> "forever"
  printfn "  Retention defaults: metrics=%s logs=%s traces=%s (compact every %dms)"
    (describeTtl retentionDefaults.metricsMs)
    (describeTtl retentionDefaults.logsMs)
    (describeTtl retentionDefaults.tracesMs)
    retentionInterval
  // Only run the compactor for pillars that are still embedded.
  let compactorMetricStore =
    if mimirUrl.IsNone then Some metricStore else None
  let compactorLogStore =
    if lokiUrl.IsNone then Some logStore else None
  let retentionCompactor =
    if compactorMetricStore.IsSome || compactorLogStore.IsSome then
      let c =
        new PulseBoard.Retention.EmbeddedCompactor(
          retentionStore,
          compactorMetricStore,
          compactorLogStore,
          retentionInterval)
      c.Start()
      Some c
    else None
  let _ = retentionCompactor   // keep alive for process lifetime

  // Storage client: every receiver path (HTTP ingest, scrape, UDP/TCP
  // listeners) writes through this. In `all` and `storage` it's a thin
  // wrapper around the in-process MetricStore/LogStore/hub; in `edge` it
  // POSTs hand-rolled protobuf to the storage tier.
  let storage : PulseBoard.Gateway.IStorageClient =
    match role with
    | "edge" ->
      let c =
        new PulseBoard.Gateway.HttpStorageClient(
          storageEndpoint.Value, edgeSecret.Value)
      c :> _
    | _ ->
      PulseBoard.Gateway.InProcessStorageClient(
        metricBackend, logBackend, traceBackend, hub) :> _

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

  // When multi-tenant + alert-eval quota is set, gate each Tick against
  // the engine's nominal tenant. Single-tenant mode runs free.
  match seedTenantSlug with
  | Some _ when multiTenant && alertBurst > 0.0 ->
    let gateTenant =
      // Use whichever tenant exists; for now this engine is global and we
      // charge the first tenant we find. A future per-tenant Engine fan-out
      // will key this properly.
      tenantStore.Tenants()
      |> Array.tryHead
      |> Option.map (fun t -> t.id)
    alertEngine.SetEvalGate(fun () ->
      match gateTenant with
      | Some tid ->
        match limiter.TryAcquire(tid, PulseBoard.Quotas.AlertEval) with
        | PulseBoard.Quotas.AcquireResult.Ok -> true
        | _ -> false
      | None -> true)
  | _ -> ()

  alertEngine.Add
    { name = "cpu-high"; metric = "cpu"; cmp = Gt
      threshold = 0.9; durationMs = 30_000L }

  // Background timer to evaluate rules every 2s.
  let evalTimer =
    new Timer((fun _ -> try alertEngine.Tick() with _ -> ()),
              null, TimeSpan.FromSeconds 2., TimeSpan.FromSeconds 2.)

  // -- Nightly audit-log S3 export (PLAN.md Phase 1 step 4) ----------------
  // Opt-in: requires both Postgres (durable audit source) and --audit-s3-bucket.
  // Credentials use the AWS default chain (env / shared config / IAM role);
  // never accept inline secrets via CLI. Endpoint override exists for
  // S3-compatible stores (MinIO, Ceph) used in tests.
  let auditS3Bucket   = envOr "PULSE_AUDIT_S3_BUCKET"   (argValue "--audit-s3-bucket=")
  let auditS3Prefix   = envOr "PULSE_AUDIT_S3_PREFIX"   (argValue "--audit-s3-prefix=")
                        |> Option.defaultValue ""
  let auditS3Region   = envOr "PULSE_AUDIT_S3_REGION"   (argValue "--audit-s3-region=")
  let auditS3Endpoint = envOr "PULSE_AUDIT_S3_ENDPOINT" (argValue "--audit-s3-endpoint=")
  let auditExportTimer : Timer option =
    match pgConn, auditS3Bucket with
    | Some cs, Some bucket ->
      let cfg : PulseBoard.S3AuditExporter.Config =
        { connectionString = cs
          bucket           = bucket
          prefix           = auditS3Prefix
          region           = auditS3Region
          endpoint         = auditS3Endpoint
          intervalMinutes  = 60 }
      printfn "  AuditExport: s3://%s/%s (region=%s endpoint=%s; first run in 30s, then hourly)"
        bucket auditS3Prefix
        (defaultArg auditS3Region "<default>")
        (defaultArg auditS3Endpoint "<aws>")
      Some (PulseBoard.S3AuditExporter.start cfg)
    | None, Some _ ->
      eprintfn "  [WARN] --audit-s3-bucket set but --postgres is not; nightly audit export disabled"
      None
    | _ -> None

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

  let ingestQuotas : PulseBoard.Ingest.IngestQuotas option =
    if multiTenant then
      Some { limiter = limiter; auditLog = auditLog }
    else None
  let ingestInner =
    PulseBoard.Ingest.webPart storage ingestQuotas
  let queryInner =
    PulseBoard.Query.webPart  metricStore logStore

  let adminInner = PulseBoard.Admin.webPart tenantStore quotaStore metricBackend retentionStore auditLog

  // -- Prometheus scrape mode (PLAN.md Phase 2 step 3) --------------------
  // Tenant-defined scrape targets; background worker fans out HTTP GETs
  // and writes through the same MetricStore as remote_write.
  let scrapeRepo : PulseBoard.PromScrape.IScrapeRepo =
    PulseBoard.PromScrape.InMemoryScrapeRepo() :> _
  let scrapeHttp =
    let h = new System.Net.Http.HttpClient()
    h.Timeout <- TimeSpan.FromSeconds 10.0
    h
  let scraper =
    if multiTenant then
      let deps : PulseBoard.PromScrape.ScrapeDeps =
        { repo       = scrapeRepo
          storage    = storage
          quotas     = ingestQuotas
          httpClient = scrapeHttp }
      let s = new PulseBoard.PromScrape.Scraper(deps)
      s.Start()
      Some s
    else None
  let scrapeAdminInner =
    PulseBoard.PromScrape.adminWebPart scrapeRepo tenantStore auditLog

  // -- StatsD UDP + Carbon plaintext TCP (PLAN.md Phase 2 step 5) ----------
  // Each listener owns one port; traffic is attributed to the owning
  // tenant. Repo is in-memory; lifecycle wraps actual socket binds.
  let listenerRepo : PulseBoard.Listeners.IListenerRepo =
    PulseBoard.Listeners.InMemoryListenerRepo() :> _
  let listenerManager : PulseBoard.Listeners.Manager option =
    if multiTenant then
      let deps : PulseBoard.Listeners.ListenerDeps =
        { repo    = listenerRepo
          storage = storage
          quotas  = ingestQuotas }
      let m = new PulseBoard.Listeners.Manager(deps)
      m.StartAll()  // no-op now (in-memory repo is empty); future Pg repo wins
      Some m
    else None
  let listenerAdminInner : WebPart =
    match listenerManager with
    | Some m -> PulseBoard.Listeners.adminWebPart m tenantStore auditLog
    | None   -> fun _ -> async { return None }

  // Build OIDC routes + session-resolving middleware (only if configured).
  let oidcRoutes, resolveSession =
    match oidcConfig with
    | Some cfg ->
      let routes, mw = PulseBoard.Oidc.build cfg tenantStore
      Some routes, mw
    | None -> None, (fun inner -> inner)

  let ingest =
    pathStarts "/ingest" >=>
      (if multiTenant then
         resolveSession (
           PulseBoard.Auth.resolveApiKey tenantStore
             (PulseBoard.Rbac.requireScope auditLog
                "ingest" PulseBoard.Tenancy.Scope.Ingest
                (PulseBoard.Rbac.requireQuota auditLog limiter
                   PulseBoard.Quotas.Ingest 1.0 ingestInner)))
       else
         PulseBoard.Auth.protect tokens ingestInner)

  // Compatibility receivers (Prom remote_write, OTLP, Loki push) share
  // the ingest auth/scope/quota chain but live at well-known external
  // paths, so factor the gate out so each new receiver only wires its
  // handler.
  let protectIngest (inner : WebPart) : WebPart =
    if multiTenant then
      resolveSession (
        PulseBoard.Auth.resolveApiKey tenantStore
          (PulseBoard.Rbac.requireScope auditLog
             "ingest" PulseBoard.Tenancy.Scope.Ingest
             (PulseBoard.Rbac.requireQuota auditLog limiter
                PulseBoard.Quotas.Ingest 1.0 inner)))
    else
      PulseBoard.Auth.protect tokens inner

  let promRemoteWriteInner =
    PulseBoard.PromRemoteWrite.handler storage ingestQuotas
  let promRemoteWrite : WebPart =
    POST >=> choose [
      path "/api/v1/write"     // Prometheus standard
      path "/api/prom/push"    // Cortex / Mimir convention
    ] >=> protectIngest promRemoteWriteInner

  // OTLP/HTTP receivers. Distinct paths per signal (metrics/logs/traces)
  // because each carries a different ExportXServiceRequest protobuf and
  // we want per-signal protect wrappers for clean audit lines.
  let otlpMetricsInner = PulseBoard.Otlp.metrics storage ingestQuotas
  let otlpLogsInner    = PulseBoard.Otlp.logs    storage ingestQuotas
  let otlpTracesInner  = PulseBoard.Otlp.traces  storage rawTraceBackend
  let otlp : WebPart =
    POST >=> choose [
      path "/v1/metrics" >=> protectIngest otlpMetricsInner
      path "/v1/logs"    >=> protectIngest otlpLogsInner
      path "/v1/traces"  >=> protectIngest otlpTracesInner
    ]

  // Grafana Loki push (Promtail / Alloy / Vector / fluent-bit).
  let lokiPushInner = PulseBoard.LokiPush.handler storage ingestQuotas
  let lokiPush : WebPart =
    POST >=> path "/loki/api/v1/push" >=> protectIngest lokiPushInner

  // Scrape admin endpoints share the admin scope gate; prepend them in
  // the choose so they win before Admin.webPart's catch-all NOT_FOUND.
  let adminAll = choose [ scrapeAdminInner; listenerAdminInner; adminInner ]

  let admin : WebPart =
    if multiTenant then
      pathStarts "/api/admin/" >=>
        resolveSession (
          PulseBoard.Auth.resolveApiKey tenantStore
            (PulseBoard.Rbac.requireScope auditLog
               "admin" PulseBoard.Tenancy.Scope.Admin adminAll))
    else
      // No admin surface in single-tenant mode — fall through to NOT_FOUND.
      fun _ -> async { return None }

  let query : WebPart =
    if multiTenant then
      pathStarts "/api/" >=>
        resolveSession (
          PulseBoard.Auth.resolveApiKey tenantStore
            (PulseBoard.Rbac.requireScope auditLog
               "query" PulseBoard.Tenancy.Scope.Query
               (PulseBoard.Rbac.requireQuota auditLog limiter
                  PulseBoard.Quotas.Query 1.0 queryInner)))
    else
      queryInner

  // Internal protocol endpoints: when `--edge-secret` is set and we are
  // running a storage-capable role, expose the HMAC-protected
  // /_internal/v1/* routes so a paired edge process can push protobuf
  // batches into our MetricStore / LogStore / hub. Edge-only processes
  // skip this (nothing to host).
  let internalRoutes : WebPart =
    match edgeSecret, role with
    | Some secret, ("storage" | "all") ->
      let inproc =
        PulseBoard.Gateway.InProcessStorageClient(
          metricBackend, logBackend, traceBackend, hub)
        :> PulseBoard.Gateway.IStorageClient
      PulseBoard.Gateway.internalWebPart inproc secret
    | _ -> fun _ -> async { return None }

  let app : WebPart =
    choose [
      internalRoutes    // unauthenticated by API key — HMAC-guarded inside
      ingest
      promRemoteWrite   // must precede `query` because /api/v1/write also matches /api/
      otlp              // /v1/* doesn't overlap /api/, but keep grouped with the other ingest receivers
      lokiPush          // /loki/api/v1/push — same
      admin     // must precede `query` because /api/admin/* also matches /api/
      query
      (match oidcRoutes with Some r -> r | None -> fun _ -> async { return None })
      path "/ws"   >=> handShake (Hub.handler hub)
      GET >=> path "/"      >=> Files.browseFile wwwroot "index.html"
      GET >=> path "/index.html" >=> Files.browseFile wwwroot "index.html"
      GET >=> path "/admin"      >=> Files.browseFile wwwroot "admin.html"
      GET >=> path "/admin.html" >=> Files.browseFile wwwroot "admin.html"
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
    printfn "  Quotas: ingest=%g rps (burst %g), query=%g rps (burst %g) per tenant."
      ingestRps ingestBurst queryRps queryBurst
    if logBurst > 0.0 then
      printfn "  Quotas: logBytes=%g B/s (burst %g B) per tenant." logBps logBurst
    if alertBurst > 0.0 then
      printfn "  Quotas: alertEval=%g rps (burst %g) per tenant." alertRps alertBurst
    if cardinalityCap > 0 then
      printfn "  Quotas: cardinality cap=%d active series per tenant." cardinalityCap
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
  printfn "  POST /api/v1/write     (Prometheus remote_write 1.0, snappy-protobuf)"
  printfn "  POST /api/prom/push    (alias of /api/v1/write)"
  printfn "  POST /v1/metrics       (OTLP/HTTP metrics, protobuf)"
  printfn "  POST /v1/logs          (OTLP/HTTP logs, protobuf)"
  printfn "  POST /v1/traces        (OTLP/HTTP traces, protobuf; counted only until Phase 3)"
  printfn "  POST /loki/api/v1/push (Grafana Loki; JSON or snappy-protobuf)"
  printfn "  GET  /api/metrics      (list)"
  printfn "  GET  /api/metrics/<n>?sinceMs=...   (series)"
  printfn "  GET  /api/logs?tail=N"
  if multiTenant then
    printfn "  GET  /api/admin/audit?tail=N        (Admin scope)"
    printfn "  GET  /api/admin/tenants              (Admin scope)"
    printfn "  POST /api/admin/tenants              (Admin scope, JSON {slug})"
    printfn "  GET  /api/admin/tenants/<id>/api-keys (Admin scope)"
    printfn "  POST /api/admin/tenants/<id>/api-keys (Admin scope, JSON {label,role,scopes?})"
    printfn "  GET  /api/admin/tenants/<id>/users    (Admin scope)"
    printfn "  PATCH /api/admin/users/<id>           (Admin scope, JSON {role})"
    printfn "  GET  /api/admin/tenants/<id>/quotas   (Admin scope)"
    printfn "  PUT  /api/admin/tenants/<id>/quotas   (Admin scope, JSON per-kind overrides)"
    printfn "  GET  /api/admin/tenants/<id>/scrape-targets  (Admin scope)"
    printfn "  POST /api/admin/tenants/<id>/scrape-targets  (Admin scope, JSON {url,intervalSec?,labels?,bearerToken?})"
    printfn "  GET  /api/admin/scrape-targets/<id>          (Admin scope)"
    printfn "  DELETE /api/admin/scrape-targets/<id>        (Admin scope)"
    printfn "  GET  /api/admin/tenants/<id>/listeners       (Admin scope)"
    printfn "  POST /api/admin/tenants/<id>/listeners       (Admin scope, JSON {protocol,port,bindAddr?})"
    printfn "  GET  /api/admin/listeners/<id>               (Admin scope)"
    printfn "  DELETE /api/admin/listeners/<id>             (Admin scope)"
    printfn "  GET  /admin                            (Admin UI)"
  match oidcConfig with
  | Some cfg ->
    printfn "  OIDC SSO: issuer=%s  client=%s  tenant=%s  cookie.secure=%b"
      cfg.issuer cfg.clientId cfg.tenantSlug cfg.cookieSecure
    printfn "  GET  /auth/login[?returnTo=/path]"
    printfn "  GET  /auth/callback"
    printfn "  ANY  /auth/logout[?returnTo=/path]"
    printfn "  GET  /auth/me"
    if oidcClientSec.IsNone then
      printfn "  (public client — PKCE only, no client_secret)"
    match cfg.defaultRole with
    | Some r ->
      printfn "  OIDC default role for new users: %A" r
    | None ->
      printfn "  OIDC default role: deny (unmapped users get 403)"
    if not (Map.isEmpty cfg.roleOverrides) then
      printfn "  OIDC role overrides: %d email(s)" cfg.roleOverrides.Count
  | None -> ()
  printfn "  WS   /ws               (live feed)"
  printfn "  GET  /                 (dashboard)"

  startWebServer config app
  GC.KeepAlive evalTimer
  GC.KeepAlive flushTimer
  match auditExportTimer with Some t -> GC.KeepAlive t | None -> ()
  0

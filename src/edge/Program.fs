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

  // -- Phase 9 early dispatch: site-only and provisioner modes ------------
  // Both are completely separate from the full multi-tenant edge below.
  // They MUST run before we start allocating tenant stores / quotas /
  // ingest state, because they don't need any of it.
  let wwwrootEarly = resolveWwwRoot ()
  let argValueEarly (prefix : string) =
    argv
    |> Array.tryFind (fun a -> a.StartsWith prefix)
    |> Option.map (fun a -> a.Substring prefix.Length)
  let envOrEarly (envName : string) (cli : string option) =
    match cli with
    | Some v -> Some v
    | None ->
      let v = Environment.GetEnvironmentVariable envName
      if String.IsNullOrWhiteSpace v then None else Some v

  if argv |> Array.contains "--site-only" then
    let provUrl = envOrEarly "PULSE_PROVISIONER_URL" (argValueEarly "--provisioner-url=")
    PulseBoard.SiteOnly.run port wwwrootEarly provUrl
    exit 0

  if argv |> Array.exists (fun a -> a = "--mode=provisioner") then
    let rootDomain =
      envOrEarly "PULSE_ROOT_DOMAIN" (argValueEarly "--root-domain=")
      |> Option.defaultValue "pulseboard.cloud"
    let flyToken = envOrEarly "FLY_API_TOKEN"  (argValueEarly "--fly-token=")
    let flyOrg   = envOrEarly "FLY_ORG_SLUG"   (argValueEarly "--fly-org=")
    let dryRun   = argv |> Array.contains "--dry-run"
    let image    =
      envOrEarly "PULSE_WORKSPACE_IMAGE" (argValueEarly "--workspace-image=")
      |> Option.defaultValue "registry.fly.io/pulseboard1:latest"
    let region   =
      envOrEarly "PULSE_FLY_REGION" (argValueEarly "--fly-region=")
      |> Option.defaultValue "iad"
    // Workspace guest sizing. 256 MB is not enough headroom for .NET +
    // Suave + Npgsql + the OIDC/JWT libs; new workspaces OOM-kill on
    // the first real request. 1 GB still wasn't enough — Server GC
    // happily expands the heap to ~75% of the cgroup and combined with
    // shared-buffer/native overhead the process easily peaks past
    // 900 MB RSS. Default to 2 GB and also pin DOTNET_GCHeapHardLimit
    // to ~50% of the guest so the heap can't drag the whole machine
    // into the OOM killer. Override per-deploy with
    // PULSE_WORKSPACE_MEM_MB / PULSE_WORKSPACE_CPUS.
    let workspaceMemMb =
      envOrEarly "PULSE_WORKSPACE_MEM_MB" (argValueEarly "--workspace-mem-mb=")
      |> Option.bind (fun s ->
           match Int32.TryParse s with true, n when n > 0 -> Some n | _ -> None)
      |> Option.defaultValue 2048
    let workspaceCpus =
      envOrEarly "PULSE_WORKSPACE_CPUS" (argValueEarly "--workspace-cpus=")
      |> Option.bind (fun s ->
           match Int32.TryParse s with true, n when n > 0 -> Some n | _ -> None)
      |> Option.defaultValue 1
    let provPgConn =
      envOrEarly "PULSE_POSTGRES" (argValueEarly "--postgres=")
    let provPublicUrl =
      envOrEarly "PULSE_PROVISIONER_PUBLIC_URL" (argValueEarly "--provisioner-public-url=")
    let provHeartbeats : PulseBoard.Provisioner.IHeartbeatStore =
      match provPgConn with
      | Some cs -> PulseBoard.PgWorkspaceRegistry.PgHeartbeatStore(cs) :> _
      | None    -> PulseBoard.Provisioner.InMemoryHeartbeatStore() :> _
    let fly : PulseBoard.Provisioner.IFlyClient =
      if dryRun then PulseBoard.Provisioner.DryRunFlyClient() :> _
      else
        match flyToken, flyOrg with
        | Some t, Some o ->
          new PulseBoard.Provisioner.HttpFlyClient(t, o, provPgConn) :> _
        | _ ->
          eprintfn "  [ERROR] --mode=provisioner without --dry-run requires FLY_API_TOKEN and FLY_ORG_SLUG (or --fly-token=/--fly-org=)"
          exit 2
    let provRegistry : PulseBoard.Provisioner.IWorkspaceRegistry =
      match provPgConn with
      | Some cs ->
        try
          PulseBoard.PgWorkspaceRegistry.ensureSchema cs
          printfn "  Registry:    Postgres (schema ensured)"
          PulseBoard.PgWorkspaceRegistry.PgWorkspaceRegistry(cs) :> _
        with ex ->
          eprintfn "  [ERROR] failed to initialise Postgres workspace registry: %s" ex.Message
          exit 2
      | None ->
        printfn "  Registry:    in-memory (ephemeral — pass --postgres=... to persist)"
        PulseBoard.Provisioner.InMemoryWorkspaceRegistry() :> _
    match provPgConn with
    | Some _ ->
      printfn "  TenantStore: Postgres (per-workspace schema pb_<slug> on shared cluster)"
    | None ->
      printfn "  TenantStore: in-memory in each workspace (no --postgres passed to provisioner)"
    let cfg : PulseBoard.Provisioner.ProvisionerConfig =
      { fly           = fly
        dryRun        = dryRun
        registry      = provRegistry
        rootDomain    = rootDomain.ToLowerInvariant()
        machineConfig =
          { image     = image
            region    = region
            // Pin the .NET GC heap to ~50% of the guest. Server GC
            // otherwise expands to ~75% of the cgroup and, combined
            // with native/JIT/Npgsql buffers, pushes the workspace
            // into the kernel OOM killer. Value is in bytes per
            // DOTNET_GCHeapHardLimit docs. Operators can still
            // override by passing the same key in --workspace-env or
            // (eventually) per-tenant config.
            envExtra  =
              Map.ofList [
                "DOTNET_GCHeapHardLimit",
                  sprintf "0x%x" (int64 workspaceMemMb * 1024L * 1024L / 2L)
              ]
            sizeCpus  = workspaceCpus
            sizeMemMb = workspaceMemMb }
        adminTokens   =
          // Operator bearer tokens for /admin/*. Comma- or whitespace-
          // separated. Empty → admin portal disabled (every /admin/*
          // returns 404, indistinguishable from a typo).
          let raw =
            envOrEarly "PULSE_ADMIN_TOKENS" (argValueEarly "--admin-tokens=")
            |> Option.defaultValue ""
          raw.Split([| ','; ';'; ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
          |> Array.map (fun s -> s.Trim())
          |> Array.filter (fun s -> s.Length > 0)
          |> Set.ofArray
        postgresConn  = provPgConn
        heartbeats    = provHeartbeats
        provisionerPublicUrl = provPublicUrl
        adminOidc     =
          // OIDC config for the admin portal. All four core fields must
          // be present for OIDC to activate; missing any one leaves the
          // portal bearer-only. Allowlist of emails and/or domains is
          // additive — a login is accepted if either matches.
          let issuer     = envOrEarly "PULSE_ADMIN_OIDC_ISSUER"        (argValueEarly "--admin-oidc-issuer=")
          let clientId   = envOrEarly "PULSE_ADMIN_OIDC_CLIENT_ID"     (argValueEarly "--admin-oidc-client-id=")
          let clientSecret = envOrEarly "PULSE_ADMIN_OIDC_CLIENT_SECRET" (argValueEarly "--admin-oidc-client-secret=")
          let redirectUri = envOrEarly "PULSE_ADMIN_OIDC_REDIRECT_URI" (argValueEarly "--admin-oidc-redirect-uri=")
          let splitCsv (raw : string) =
            raw.Split([| ','; ';'; ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun s -> s.Trim().ToLowerInvariant())
            |> Array.filter (fun s -> s.Length > 0)
            |> Set.ofArray
          let emails =
            envOrEarly "PULSE_ADMIN_OIDC_ALLOWED_EMAILS"  (argValueEarly "--admin-oidc-allowed-emails=")
            |> Option.defaultValue "" |> splitCsv
          let domains =
            envOrEarly "PULSE_ADMIN_OIDC_ALLOWED_DOMAINS" (argValueEarly "--admin-oidc-allowed-domains=")
            |> Option.defaultValue "" |> splitCsv
          let sessionSecret =
            envOrEarly "PULSE_ADMIN_OIDC_SESSION_SECRET" (argValueEarly "--admin-oidc-session-secret=")
          match issuer, clientId, redirectUri with
          | Some iss, Some cid, Some ruri when (not (Set.isEmpty emails)) || (not (Set.isEmpty domains)) ->
            // Session key: prefer operator-supplied (base64) for cookie
            // continuity across restarts; otherwise generate a fresh one
            // (sessions invalidated on each provisioner restart, which is
            // acceptable for human SSO since they can re-auth in seconds).
            let key =
              match sessionSecret with
              | Some s ->
                try PulseBoard.Session.keyFromBase64 s
                with ex ->
                  eprintfn "  [WARN] PULSE_ADMIN_OIDC_SESSION_SECRET invalid (%s); generating ephemeral key" ex.Message
                  PulseBoard.Session.generateKey ()
              | None -> PulseBoard.Session.generateKey ()
            let secure = ruri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            Some
              ({ issuer         = iss.TrimEnd '/'
                 clientId       = cid
                 clientSecret   = clientSecret
                 redirectUri    = ruri
                 allowedEmails  = emails
                 allowedDomains = domains
                 sessionKey     = key
                 sessionTtl     = TimeSpan.FromHours 12.0
                 cookieSecure   = secure } : PulseBoard.AdminOidc.Config)
          | _ -> None }
    if Set.isEmpty cfg.adminTokens && cfg.adminOidc.IsNone then
      printfn "  Admin:       disabled (set PULSE_ADMIN_TOKENS and/or PULSE_ADMIN_OIDC_* to enable /admin/*)"
    else
      let bearerInfo =
        if Set.isEmpty cfg.adminTokens then "no bearer"
        else sprintf "%d bearer token(s)" cfg.adminTokens.Count
      let oidcInfo =
        match cfg.adminOidc with
        | None -> "no OIDC"
        | Some oc ->
          sprintf "OIDC via %s (%d email(s), %d domain(s))"
            oc.issuer (Set.count oc.allowedEmails) (Set.count oc.allowedDomains)
      printfn "  Admin:       enabled — %s, %s" bearerInfo oidcInfo
    match provPublicUrl with
    | Some url -> printfn "  Heartbeats:  workspaces ping %s/provision/heartbeat (PULSE_PROVISIONER_URL injected)" url
    | None     -> printfn "  Heartbeats:  PULSE_PROVISIONER_PUBLIC_URL not set; workspaces won't heartbeat (admin portal will show 'never')"
    PulseBoard.Provisioner.run port cfg
    exit 0

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

  // -- Downsampling rollups (PLAN.md Phase 3 step 4) -------------------
  // Background job re-aggregates the embedded MetricStore into
  // 1m / 5m / 1h buckets so wide-window queries don't have to scan
  // raw points. Skipped when metrics are in cloud mode (Mimir does
  // this via its own recording rules / blocks compactor).
  let rollupsEnabled =
    match envOr "PULSE_ROLLUPS_ENABLED" (argValue "--rollups-enabled=") with
    | Some v ->
      match v.Trim().ToLowerInvariant() with
      | "0" | "false" | "no" | "off" -> false
      | _ -> true
    | None -> true
  let rollupInterval =
    parseTtlMs "PULSE_ROLLUPS_INTERVAL_MS" "--rollups-interval-ms="
    |> Option.map int
    |> Option.defaultValue 30_000
  let rollupStore, rollupWorker =
    if rollupsEnabled && mimirUrl.IsNone then
      let rs = PulseBoard.Rollups.RollupStore(maxBucketsPerSeries = 10_000)
      let w =
        new PulseBoard.Rollups.RollupWorker(
          metricStore, rs,
          PulseBoard.Rollups.allResolutions,
          rollupInterval)
      w.Start()
      printfn "  Rollups: enabled (1m/5m/1h, recompute every %dms)" rollupInterval
      Some rs, Some w
    else
      if rollupsEnabled && mimirUrl.IsSome then
        printfn "  Rollups: skipped (Mimir backend handles downsampling)"
      else
        printfn "  Rollups: disabled"
      None, None
  let _ = rollupWorker         // keep alive for process lifetime

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

  // -- Alerting pipeline (PLAN.md Phase 5) --------------------------------
  // 1. Rule engine: persisted PromQL/LogQL rule groups under
  //    `<dataDir>/rules/<tenant>/<groupId>.json`, evaluator pool with
  //    group-id shard hashing, `pulse_rule_eval_seconds` metric.
  // 2. Alertmanager-equivalent: per-tenant config under
  //    `<dataDir>/routing/<tenant>.json` (route tree, receivers,
  //    silences, inhibitions, mute time intervals). Routing/grouping/
  //    dedup happen here.
  // 3. Notify pipeline reliability: persistent NDJSON outbound queue
  //    under `<dataDir>/notify/` with retry/backoff and a sibling
  //    dead-letter file.
  //
  // The legacy `--webhook=` / `--slack=` URLs are migrated into the
  // default routing config as auto-named receivers, so out-of-the-box
  // behaviour is preserved when no Alertmanager config has been
  // persisted yet.

  let routingStore : PulseBoard.Routing.IConfigStore =
    PulseBoard.Routing.FileConfigStore(Path.Combine(dataDir, "routing")) :> _
  let notifyQueue : PulseBoard.NotifyQueue.INotifyQueue =
    PulseBoard.NotifyQueue.FileNotifyQueue(Path.Combine(dataDir, "notify")) :> _
  let ruleStore : PulseBoard.Rules.IRuleStore =
    PulseBoard.Rules.FileRuleStore(Path.Combine(dataDir, "rules")) :> _

  // Seed default rule group + default routing config (with legacy
  // webhook/slack URLs lifted into receivers) for single-tenant mode.
  let bootstrapAlerting (tid : PulseBoard.Tenancy.TenantId) =
    PulseBoard.Rules.seedIfEmpty ruleStore tid
    let cfg = routingStore.Get tid
    let needsSeed =
      cfg.receivers.Length = 0
      && (webhookUrls.Length > 0 || slackUrls.Length > 0
          || cfg.route.receiverId.IsNone)
    if needsSeed then
      let mkReceiver i (typ : string) (url : string) : PulseBoard.Routing.Receiver =
        { id = sprintf "%s-%d" typ i
          name = sprintf "%s-%d" typ i
          type_ = typ
          url = Some url
          secret = None
          extra = Map.empty }
      let receivers =
        [
          yield! webhookUrls |> List.mapi (fun i u -> mkReceiver i "webhook" u)
          yield! slackUrls   |> List.mapi (fun i u -> mkReceiver i "slack" u)
        ]
        |> List.toArray
      let firstId =
        receivers |> Array.tryHead |> Option.map (fun r -> r.id)
      let route =
        { cfg.route with receiverId = firstId }
      routingStore.Set(tid, { cfg with route = route; receivers = receivers })
  if not multiTenant then
    bootstrapAlerting (PulseBoard.Tenancy.TenantId "__local__")

  // Hub sink — broadcasts firing alerts to the WS hub so the live page
  // and dashboards see them in real time. Schema matches the legacy
  // `{type:"alert",...}` shape so existing clients keep working.
  let hubAlertSink (a : PulseBoard.Rules.AlertInstance) =
    if a.state = PulseBoard.Rules.AlertState.Firing then
      let payload =
        sprintf """{"type":"alert","rule":%s,"value":%f,"firedAt":%d,"severity":%s,"labels":%s}"""
          (System.Text.Json.JsonSerializer.Serialize a.ruleName)
          a.value
          (a.firedAt |> Option.defaultValue (nowMs ()))
          (System.Text.Json.JsonSerializer.Serialize (PulseBoard.Rules.severityToStr a.severity))
          (System.Text.Json.JsonSerializer.Serialize a.labels)
      hub.Publish payload

  let consoleAlertSink (a : PulseBoard.Rules.AlertInstance) =
    let label = PulseBoard.Rules.alertStateToStr a.state
    printfn "[ALERT:%s] %s value=%f labels=%A"
      label a.ruleName a.value a.labels

  let alertingPipeline =
    PulseBoard.Routing.Pipeline(routingStore, notifyQueue, metricStore)

  // On-call schedules + escalation policies + acks (PLAN.md Phase 5 #4).
  let onCallCatalog : PulseBoard.OnCall.ICatalogStore =
    PulseBoard.OnCall.FileCatalogStore(Path.Combine(dataDir, "oncall")) :> _
  let onCallAcks    : PulseBoard.OnCall.IAckStore =
    PulseBoard.OnCall.FileAckStore(Path.Combine(dataDir, "acks")) :> _
  let escalator     = PulseBoard.OnCall.Escalator(onCallCatalog, onCallAcks)
  alertingPipeline.SetEscalator(escalator :> PulseBoard.Routing.IEscalator)

  let alertSink =
    { new PulseBoard.Rules.IAlertSink with
        member _.OnAlert a =
          try consoleAlertSink a with _ -> ()
          try hubAlertSink a    with _ -> ()
          try alertingPipeline.OnAlert a with ex ->
            eprintfn "[routing] OnAlert failed: %s" ex.Message }

  let ruleEvaluator =
    PulseBoard.Rules.Evaluator(metricStore, logStore, ruleStore, alertSink, metricStore)
  ruleEvaluator.SetTenantsProvider(fun () ->
    if multiTenant then
      tenantStore.Tenants() |> Array.map (fun t -> t.id)
    else
      [| PulseBoard.Tenancy.TenantId "__local__" |])
  ruleEvaluator.Start()

  // Outbound queue workers.
  let notifyCts = new CancellationTokenSource()
  let notifyWorkers =
    [ for _ in 1 .. 2 ->
        PulseBoard.NotifyQueue.runWorker
          notifyQueue (Some metricStore)
          1_000L 60_000L notifyCts.Token ]

  let rulesInner       = PulseBoard.Rules.webPart       multiTenant ruleStore ruleEvaluator
  let routingInner     = PulseBoard.Routing.webPart     multiTenant routingStore
  let notifyQueueInner = PulseBoard.NotifyQueue.webPart multiTenant notifyQueue
  let onCallInner      = PulseBoard.OnCall.webPart      multiTenant onCallCatalog onCallAcks

  printfn "  Alerting: rule store under %s; routing under %s; queue under %s"
    (Path.Combine(dataDir, "rules"))
    (Path.Combine(dataDir, "routing"))
    (Path.Combine(dataDir, "notify"))
  printfn "  OnCall:   catalog under %s; acks under %s"
    (Path.Combine(dataDir, "oncall"))
    (Path.Combine(dataDir, "acks"))

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

  // -- Workspace heartbeat client (PLAN.md Phase 9 step 6) -----------------
  // When this process is itself a workspace machine spawned by the
  // provisioner (multi-tenant + PULSE_SLUG + PULSE_PROVISIONER_URL all
  // set), fire-and-forget a background loop that POSTs
  // `{slug, version}` to <provisioner>/provision/heartbeat every 15s.
  // The provisioner is on flycast-only, so no auth is needed; failures
  // are swallowed (heartbeats are informational only).
  if multiTenant then
    let hbSlug = Environment.GetEnvironmentVariable "PULSE_SLUG"
    let hbProv = Environment.GetEnvironmentVariable "PULSE_PROVISIONER_URL"
    if not (String.IsNullOrWhiteSpace hbSlug)
       && not (String.IsNullOrWhiteSpace hbProv) then
      let version =
        try System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString()
        with _ -> "unknown"
      let url = hbProv.TrimEnd('/') + "/provision/heartbeat"
      let body =
        sprintf """{"slug":%s,"version":%s}"""
          (System.Text.Json.JsonSerializer.Serialize hbSlug)
          (System.Text.Json.JsonSerializer.Serialize version)
      printfn "  Heartbeat:   posting to %s every 15s (slug=%s)" url hbSlug
      let http = new System.Net.Http.HttpClient(Timeout = TimeSpan.FromSeconds 5.0)
      let loop = async {
        // Tiny initial jitter so a fleet of synchronised restarts
        // doesn't dogpile the provisioner.
        do! Async.Sleep (Random().Next(0, 5000))
        while true do
          try
            use content = new System.Net.Http.StringContent(
                            body, System.Text.Encoding.UTF8, "application/json")
            let! _ = http.PostAsync(url, content) |> Async.AwaitTask
            ()
          with _ -> () // swallow; informational only
          do! Async.Sleep 15000
      }
      Async.Start loop
    else
      printfn "  Heartbeat:   disabled (PULSE_SLUG and PULSE_PROVISIONER_URL must both be set)"

  // -- Route composition ------------------------------------------------------

  // -- Secrets / envelope encryption (PLAN.md Phase 6 #4) ------------------
  // KEK is loaded from PULSE_MASTER_KEY (base64, 32 bytes) when set,
  // otherwise auto-generated at <dataDir>/secrets/master.key. Per-tenant
  // DEKs live next to it. Inline [[pii:<value>]] markers in log messages
  // are encrypted on ingest using the tenant's DEK; decryption is
  // Admin-only via POST /api/secrets/decrypt.
  let secretsDir = Path.Combine(dataDir, "secrets")
  let kek = PulseBoard.Secrets.loadOrCreateKek secretsDir
  let secretsStore : PulseBoard.Secrets.ISecretsStore =
    PulseBoard.Secrets.FileSecretsStore(secretsDir, kek) :> _
  let piiPolicyStore : PulseBoard.Secrets.IPiiPolicyStore =
    PulseBoard.Secrets.FilePiiPolicyStore(secretsDir) :> _
  printfn "  Secrets: KEK + per-tenant DEKs at %s (PII markers auto-encrypted)" secretsDir

  let ingestQuotas : PulseBoard.Ingest.IngestQuotas option =
    if multiTenant then
      Some { limiter = limiter; auditLog = auditLog }
    else None
  // Phase 7 #1 — billing meter lives here so ingest receivers can record
  // usage; providers + rollup loop + admin endpoints are wired further
  // below once the full admin pipeline is composed.
  let billingMeter : PulseBoard.Billing.IBillingMeter =
    PulseBoard.Billing.InMemoryBillingMeter() :> _
  // Phase 8 #1 — per-series cost tracker tapped from /ingest/metrics.
  let costTracker : PulseBoard.Costs.ICostTracker =
    PulseBoard.Costs.InMemoryCostTracker() :> _
  let ingestInner =
    PulseBoard.Ingest.webPart storage ingestQuotas (Some secretsStore) (Some billingMeter) (Some costTracker)
  let queryInner =
    PulseBoard.Query.webPart  metricStore logStore rollupStore

  // -- Prometheus / Loki query APIs (PLAN.md Phase 4 step 1) ---------
  // When the metric / log pillar is wired to a cloud backend we
  // forward the standard HTTP query surface to the upstream verbatim;
  // otherwise we serve an embedded subset (vector selectors only for
  // PromQL, stream selectors + a single |= / != filter for LogQL).
  let promUpstream : PulseBoard.QueryApi.Upstream option =
    mimirUrl
    |> Option.map (fun u ->
      { baseUrl   = u
        orgHeader = optOrg mimirOrg
        bearer    = mimirToken })
  let lokiUpstream : PulseBoard.QueryApi.Upstream option =
    lokiUrl
    |> Option.map (fun u ->
      { baseUrl   = u
        orgHeader = optOrg lokiOrg
        bearer    = lokiToken })
  let queryApiInner =
    PulseBoard.QueryApi.webPart
      promUpstream lokiUpstream metricStore rollupStore logStore
  let describeQueryBackend (label : string)
                            (u : PulseBoard.QueryApi.Upstream option) =
    match u with
    | Some up -> printfn "  %s: proxy -> %s" label up.baseUrl
    | None    -> printfn "  %s: embedded (vector / stream selectors only)" label
  describeQueryBackend "PromQL API" promUpstream
  describeQueryBackend "LogQL  API" lokiUpstream

  // -- Dashboards (PLAN.md Phase 4 step 2) --------------------------------
  // File-backed per-tenant dashboard store. Auto-seeds an overview
  // dashboard the first time each tenant is observed (single-tenant
  // mode pins everything to `singleTenantId`).
  let dashboardRepo : PulseBoard.Dashboards.IDashboardRepo =
    PulseBoard.Dashboards.FileDashboardRepo(Path.Combine(dataDir, "dashboards")) :> _
  if not multiTenant then
    PulseBoard.Dashboards.seedIfEmpty dashboardRepo PulseBoard.Dashboards.singleTenantId
  let dashboardsInner =
    PulseBoard.Dashboards.webPart multiTenant dashboardRepo
  printfn "  Dashboards: file-backed at %s"
    (Path.Combine(dataDir, "dashboards"))

  // -- Self-observability (PLAN.md Phase 6 #6) -----------------------------
  // Reserve the `__meta__` tenant, seed its dashboard, and start an SLO
  // derivation loop that emits `pulse_slo_*_5m` series every 30 s.
  if multiTenant then
    let metaTenant = PulseBoard.Self.bootstrap tenantStore dashboardRepo
    printfn "  Self: meta tenant '%s' (id=%s) + dashboard + SLO loop"
      metaTenant.slug
      (let (PulseBoard.Tenancy.TenantId t) = metaTenant.id in t)
    let sloCts = new System.Threading.CancellationTokenSource()
    PulseBoard.Self.startSloLoop metricStore 30 sloCts.Token |> ignore
  else
    printfn "  Self: meta tenant skipped (single-tenant mode)"


  // -- Spans / service map / RUM (PLAN.md Phase 4 step 4) ----------------
  // In-process ring of recent spans per tenant. Real persistence still
  // happens via the Tempo passthrough (`--tempo-url=`); this store
  // powers the SPA's Traces + Service Map tabs without depending on
  // an external trace backend.
  let spanStoreCapacity = 10_000
  let spanStore : PulseBoard.Spans.ISpanStore =
    PulseBoard.Spans.InMemorySpanStore(spanStoreCapacity) :> _
  let traceApiInner = PulseBoard.TraceApi.webPart multiTenant spanStore
  let rumInner      = PulseBoard.Rum.webPart      multiTenant metricStore logStore
  printfn "  Spans: in-memory ring (capacity=%d per tenant)" spanStoreCapacity
  printfn "  RUM: %s"
    (if multiTenant
     then "POST /rum/v1/<tenantId>/events (unauthenticated stub)"
     else "POST /rum/v1/events (single-tenant)")

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
  let otlpTracesInner  = PulseBoard.Otlp.traces  storage rawTraceBackend (Some spanStore)
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
  let secretsApiInner =
    PulseBoard.Admin.secretsWebPart secretsStore piiPolicyStore auditLog

  // Phase 7 #1 + #2 — billing providers + plan/usage admin endpoints.
  // The `InMemoryBillingMeter` itself is defined above so the ingest
  // receivers can record usage. Here we attach the file provider, kick
  // off the daily rollup loop, and compose the admin endpoints.
  let billingFileProvider =
    PulseBoard.Billing.FileBillingProvider(Path.Combine(dataDir, "billing"))
  let billingProviders : PulseBoard.Billing.IBillingProvider[] =
    [| billingFileProvider :> PulseBoard.Billing.IBillingProvider |]
  let billingPlanFor (tid : PulseBoard.Tenancy.TenantId) =
    match tenantStore.TryGetTenant tid with
    | Some t -> t.plan
    | None   -> PulseBoard.Tenancy.Plan.Free
  // Daily rollup loop. 86400s by default; clamped to >=5s inside Billing.
  let billingRollupCts = new System.Threading.CancellationTokenSource()
  let _billingRollupTask =
    PulseBoard.Billing.startRollupLoop
      billingMeter billingProviders billingPlanFor 86400
      billingRollupCts.Token
  let billingAdminInner =
    PulseBoard.Admin.billingWebPart
      tenantStore billingMeter billingProviders auditLog

  // Phase 8 #1 — cost transparency admin endpoints.
  let costsAdminInner = PulseBoard.Admin.costsWebPart costTracker
  // Phase 8 #3 — AI assist provider (deterministic Echo by default).
  let aiProvider : PulseBoard.AiAssist.IAiProvider =
    PulseBoard.AiAssist.EchoAiProvider() :> _

  let adminAll = choose [ scrapeAdminInner; listenerAdminInner; billingAdminInner; costsAdminInner; adminInner ]

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

  // `/api/secrets/*` lives outside `/api/admin/*` but reuses the Admin scope
  // gate so only Admin-scoped keys can encrypt/decrypt or edit PII policy.
  let secretsAdmin : WebPart =
    if multiTenant then
      pathStarts "/api/secrets/" >=>
        resolveSession (
          PulseBoard.Auth.resolveApiKey tenantStore
            (PulseBoard.Rbac.requireScope auditLog
               "admin" PulseBoard.Tenancy.Scope.Admin secretsApiInner))
    else
      fun _ -> async { return None }

  let query : WebPart =
    let aiExplainInner = PulseBoard.Admin.aiExplainWebPart aiProvider auditLog
    let combinedInner = choose [ queryApiInner; dashboardsInner; traceApiInner; rulesInner; routingInner; notifyQueueInner; onCallInner; aiExplainInner; queryInner ]
    if multiTenant then
      pathStarts "/api/" >=>
        resolveSession (
          PulseBoard.Auth.resolveApiKey tenantStore
            (PulseBoard.Rbac.requireScope auditLog
               "query" PulseBoard.Tenancy.Scope.Query
               (PulseBoard.Rbac.requireQuota auditLog limiter
                  PulseBoard.Quotas.Query 1.0 combinedInner)))
    else
      combinedInner

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
      // Liveness probe. Cheap, no auth, no DB call — used by Fly
      // http_service.checks, Caddy upstream health, and CI smoke.
      GET >=> path "/healthz" >=>
        (Successful.OK
           (sprintf """{"status":"ok","role":%s,"multiTenant":%b}"""
              (if multiTenant then "\"workspace\"" else "\"single-tenant\"")
              multiTenant)
         >=> Writers.setMimeType "application/json")
      internalRoutes    // unauthenticated by API key — HMAC-guarded inside
      (if multiTenant then
         PulseBoard.Signup.webPart
           tenantStore (PulseBoard.Signup.defaultLimiter ()) auditLog
       else fun _ -> async { return None })
      ingest
      promRemoteWrite   // must precede `query` because /api/v1/write also matches /api/
      otlp              // /v1/* doesn't overlap /api/, but keep grouped with the other ingest receivers
      lokiPush          // /loki/api/v1/push — same
      rumInner          // /rum/v1/* — unauthenticated beacon stub (Phase 4 #4)
      admin     // must precede `query` because /api/admin/* also matches /api/
      secretsAdmin   // /api/secrets/* — also Admin-scoped, sibling of admin
      PulseBoard.Admin.pricingWebPart ()   // Phase 8 #5 — public rate card + calculator
      query
      (match oidcRoutes with Some r -> r | None -> fun _ -> async { return None })
      path "/ws"   >=> handShake (Hub.handler hub)
      // In --multi-tenant (workspace) mode, "/" is a tenant-scoped landing
      // page (Sign in / Dashboard / Docs CTAs). The marketing home.html is
      // still reachable at /home for anyone deep-linking it. In single-tenant
      // mode, "/" remains the marketing page.
      GET >=> path "/"            >=> Files.browseFile wwwroot (if multiTenant then "workspace.html" else "home.html")
      GET >=> path "/index.html"   >=> Files.browseFile wwwroot "home.html"
      GET >=> path "/home"         >=> Files.browseFile wwwroot "home.html"
      GET >=> path "/workspace"    >=> Files.browseFile wwwroot "workspace.html"
      GET >=> path "/docs"         >=> Files.browseFile wwwroot "docs.html"
      GET >=> path "/docs.html"    >=> Files.browseFile wwwroot "docs.html"
      GET >=> path "/signup"       >=> Files.browseFile wwwroot "signup.html"
      GET >=> path "/signup.html"  >=> Files.browseFile wwwroot "signup.html"
      GET >=> path "/onboard"      >=> Files.browseFile wwwroot "signup.html"
      GET >=> path "/signin"       >=> Files.browseFile wwwroot "signin.html"
      GET >=> path "/signin.html"  >=> Files.browseFile wwwroot "signin.html"
      GET >=> path "/app"          >=> Files.browseFile wwwroot "index.html"
      GET >=> path "/app.html"     >=> Files.browseFile wwwroot "index.html"
      GET >=> path "/dashboard"    >=> Files.browseFile wwwroot "index.html"
      GET >=> path "/admin"        >=> Files.browseFile wwwroot "admin.html"
      GET >=> path "/admin.html"   >=> Files.browseFile wwwroot "admin.html"
      GET >=> path "/pricing"      >=> Files.browseFile wwwroot "pricing.html"
      GET >=> path "/pricing.html" >=> Files.browseFile wwwroot "pricing.html"
      GET >=> path "/live"         >=> Files.browseFile wwwroot "live.html"
      GET >=> Files.browse wwwroot
      NOT_FOUND "Not found."
    ]

  // Bind address(es): default to loopback so the OSS demo isn't exposed
  // to the LAN by accident. Containers (Fly, K8s) override via
  // PULSE_BIND_ADDR — set in the Dockerfile so every role binds publicly
  // inside the VM.
  //
  // Accepts a comma-separated list, e.g. PULSE_BIND_ADDR="::,0.0.0.0".
  // We bind every listed address as its own Suave HttpBinding because
  // .NET on Linux defaults IPv6 sockets to IPV6_V6ONLY=1 (unlike the
  // kernel's default of 0), so a single `::` listener does NOT accept
  // IPv4 traffic. Listing both `::` and `0.0.0.0` gives a real
  // dual-stack listener: Fly's loopback health check (127.0.0.1) and
  // flycast's IPv6 traffic both arrive at the same Suave instance.
  let bindAddrs =
    match Environment.GetEnvironmentVariable "PULSE_BIND_ADDR" with
    | null | "" -> [ IPAddress.Loopback ]
    | s ->
      s.Split([| ',' ; ';' ; ' ' |], StringSplitOptions.RemoveEmptyEntries)
      |> Array.choose (fun raw ->
           let t = raw.Trim()
           match IPAddress.TryParse t with
           | true, ip -> Some ip
           | _ ->
             eprintfn "  [WARN] PULSE_BIND_ADDR entry %s is not a valid IP; ignoring" t
             None)
      |> Array.toList
      |> function
         | []   ->
           eprintfn "  [WARN] PULSE_BIND_ADDR=%s yielded no valid IPs; falling back to 127.0.0.1" s
           [ IPAddress.Loopback ]
         | ips  -> ips
  let config =
    { defaultConfig with
        bindings   = bindAddrs |> List.map (fun ip -> HttpBinding.create HTTP ip (uint16 port))
        homeFolder = Some wwwroot }

  for ip in bindAddrs do
    printfn "PulseBoard listening on http://%O:%d" ip port
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
  printfn "  POST /v1/traces        (OTLP/HTTP traces, protobuf; spans stored in-memory, optional Tempo passthrough)"
  printfn "  POST /loki/api/v1/push (Grafana Loki; JSON or snappy-protobuf)"
  if multiTenant then
    printfn "  POST /rum/v1/<tenantId>/events  (RUM beacon, unauthenticated stub)"
  else
    printfn "  POST /rum/v1/events    (RUM beacon, single-tenant)"
  printfn "  GET  /api/metrics      (list)"
  printfn "  GET  /api/metrics/<n>?sinceMs=...   (series)"
  printfn "  GET  /api/logs?tail=N"
  printfn "  GET  /api/traces[?sinceMs=...&limit=N]  (recent trace summaries)"
  printfn "  GET  /api/traces/<traceId>              (full span list)"
  printfn "  GET  /api/servicemap[?sinceMs=...]      (derived service graph)"
  printfn "  GET  /api/prom/api/v1/{query,query_range,labels,label/<n>/values,series}"
  printfn "  GET  /api/loki/api/v1/{query_range,labels,label/<n>/values}"
  printfn "  GET  /api/dashboards | POST /api/dashboards | GET/PUT/DELETE /api/dashboards/<id>"
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
    printfn "  PATCH /api/admin/tenants/<id>/plan           (Admin scope, JSON {plan})"
    printfn "  GET  /api/admin/tenants/<id>/usage           (Admin scope)"
    printfn "  POST /api/admin/billing/flush                (Admin scope)"
    printfn "  POST /api/signup                       (unauthenticated, JSON {slug,email})"
    printfn "  GET  /api/wizard/snippets?lang=&apiKey=&host=  (unauthenticated)"
    printfn "  GET  /api/admin/tenants/<id>/cost/series?top=N  (Admin scope)"
    printfn "  GET  /api/admin/tenants/<id>/cost/teams         (Admin scope)"
    printfn "  POST /api/ai/explain                   (Query scope, JSON {seriesName,samples,question?})"
    printfn "  GET  /api/pricing                      (public rate card)"
    printfn "  POST /api/pricing/estimate             (public, JSON usage map)"
    printfn "  GET  /pricing                          (public calculator UI)"
    printfn "  GET  /admin                            (Admin UI)"
    printfn ""
    printfn "Public website:"
    printfn "  GET  /                                 (landing page)"
    printfn "  GET  /docs                             (documentation)"
    printfn "  GET  /signup                           (sign-up form)"
    printfn "  GET  /signin                           (sign-in form)"
    printfn "  GET  /app                              (dashboard SPA)"
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
  GC.KeepAlive ruleEvaluator
  GC.KeepAlive notifyWorkers
  GC.KeepAlive alertingPipeline
  GC.KeepAlive flushTimer
  match auditExportTimer with Some t -> GC.KeepAlive t | None -> ()
  0

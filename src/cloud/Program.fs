module PulseBoard.Program

open System
open System.IO
open System.Net
open System.Security.Cryptography

[<EntryPoint>]
let main argv =
  let port =
    match argv |> Array.tryFind (fun a -> a.StartsWith "--port=") with
    | Some s ->
      match Int32.TryParse(s.Substring(7)) with
      | true, n -> n
      | _ -> 8080
    | None -> 8080

  let resolveWwwRoot () =
    let candidates =
      [ Path.Combine(AppContext.BaseDirectory, "wwwroot")
        Path.Combine(Directory.GetCurrentDirectory(), "wwwroot")
        Path.Combine(Directory.GetCurrentDirectory(), "src", "cloud", "wwwroot") ]
    candidates
    |> List.tryFind Directory.Exists
    |> Option.defaultValue (Path.Combine(AppContext.BaseDirectory, "wwwroot"))

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

  let parseBool (raw : string option) =
    raw
    |> Option.map (fun s -> s.Trim().ToLowerInvariant())
    |> Option.exists (fun s -> s = "1" || s = "true" || s = "yes")

  let generateKey () =
    let b = Array.zeroCreate 32
    RandomNumberGenerator.Fill(Span b)
    b

  let keyFromBase64 (raw : string) =
    let normalized = raw.Trim().Replace('-', '+').Replace('_', '/')
    let padded =
      match normalized.Length % 4 with
      | 0 -> normalized
      | 2 -> normalized + "=="
      | 3 -> normalized + "="
      | _ -> normalized + "==="
    let bytes = Convert.FromBase64String padded
    if bytes.Length < 32 then
      invalidArg "raw" "session secret must be at least 32 bytes (256 bits)"
    bytes

  let wwwroot = resolveWwwRoot ()

  if argv |> Array.contains "--site-only" then
    let provUrl = envOr "PULSE_PROVISIONER_URL" (argValue "--provisioner-url=")
    let pgConn = envOr "PULSE_POSTGRES" (argValue "--postgres=")
    let enableAuth =
      argv |> Array.contains "--customer-auth"
      || parseBool (envOr "PULSE_CUSTOMER_AUTH" None)

    let customerAuth : PulseBoard.CustomerAuthApi.CustomerAuthConfig option =
      if not enableAuth then None
      else
        let store : PulseBoard.CustomerAuth.ICustomerStore =
          match pgConn with
          | Some cs ->
            try
              PulseBoard.PgCustomerStore.ensureSchema cs
              printfn "  CustomerStore: Postgres (schema ensured)"
              PulseBoard.PgCustomerStore.PgCustomerStore(cs) :> _
            with ex ->
              eprintfn "  [ERROR] failed to initialise customer Postgres: %s" ex.Message
              exit 2
          | None ->
            printfn "  CustomerStore: in-memory (ephemeral — pass --postgres=...)"
            PulseBoard.CustomerAuth.InMemoryCustomerStore() :> _
        let mailgunKey = envOr "MAILGUN_API_KEY" (argValue "--mailgun-key=")
        let mailgunDom = envOr "MAILGUN_DOMAIN" (argValue "--mailgun-domain=")
        let mailgunEu =
          argv |> Array.contains "--mailgun-eu"
          || parseBool (envOr "MAILGUN_EU" None)
        let fromAddr =
          envOr "PULSE_AUTH_FROM" (argValue "--auth-from=")
          |> Option.defaultValue "PulseBoard <no-reply@pulseboard.cloud>"
        let sender : PulseBoard.EmailSender.IEmailSender =
          match mailgunKey, mailgunDom with
          | Some k, Some d ->
            printfn "  EmailSender:   Mailgun (%s%s)" d (if mailgunEu then ", EU region" else "")
            PulseBoard.EmailSender.MailgunEmailSender(
              { apiKey = k
                domain = d
                euRegion = mailgunEu
                defaultFrom = fromAddr }) :> _
          | _ ->
            printfn "  EmailSender:   console (set MAILGUN_API_KEY+MAILGUN_DOMAIN for real delivery)"
            PulseBoard.EmailSender.ConsoleEmailSender() :> _
        let publicBase =
          envOr "PULSE_PUBLIC_BASE" (argValue "--public-base=")
          |> Option.defaultValue (sprintf "http://localhost:%d" port)
        let secureCookies =
          publicBase.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        let signingKey =
          match envOr "PULSE_CUSTOMER_JWT_SECRET" (argValue "--customer-jwt-secret=") with
          | Some raw ->
            try PulseBoard.CustomerAuthApi.keyFromBase64 raw
            with ex ->
              eprintfn "  [ERROR] bad --customer-jwt-secret: %s" ex.Message
              exit 2
          | None ->
            eprintfn "  [WARN] no --customer-jwt-secret set; generating an EPHEMERAL key (sessions die on restart)"
            PulseBoard.CustomerAuthApi.generateKey ()
        let publicBaseTrim = publicBase.TrimEnd '/'
        let ghClientId = envOr "GITHUB_OAUTH_CLIENT_ID" (argValue "--github-client-id=")
        let ghClientSecret = envOr "GITHUB_OAUTH_CLIENT_SECRET" (argValue "--github-client-secret=")
        let ghCallback =
          envOr "GITHUB_OAUTH_CALLBACK" (argValue "--github-callback=")
          |> Option.defaultValue (publicBaseTrim + "/api/auth/github/callback")
        let github : PulseBoard.GithubOAuth.GithubConfig option =
          match ghClientId, ghClientSecret with
          | Some id, Some secret ->
            printfn "  GitHubOAuth:   enabled (callback %s)" ghCallback
            Some { clientId = id; clientSecret = secret; callbackUrl = ghCallback }
          | _ ->
            printfn "  GitHubOAuth:   disabled (set GITHUB_OAUTH_CLIENT_ID + GITHUB_OAUTH_CLIENT_SECRET to enable)"
            None
        Some
          { store = store
            sender = sender
            signingKey = signingKey
            publicBase = publicBaseTrim
            fromAddress = fromAddr
            secureCookies = secureCookies
            rateLimiter = PulseBoard.CustomerAuthApi.AuthRateLimiter(30, 3600)
            github = github
            githubStates = PulseBoard.GithubOAuth.StateCache() }

    let portal : PulseBoard.PortalApi.PortalApiConfig option =
      match customerAuth with
      | None -> None
      | Some authCfg ->
        let workspaceStore : PulseBoard.PortalStore.ICustomerWorkspaceStore =
          match pgConn with
          | Some cs ->
            try
              PulseBoard.PortalStore.ensureSchema cs
              printfn "  PortalStore:   Postgres (schema ensured)"
              PulseBoard.PortalStore.PgCustomerWorkspaceStore(cs) :> _
            with ex ->
              eprintfn "  [ERROR] failed to initialise portal Postgres: %s" ex.Message
              exit 2
          | None ->
            printfn "  PortalStore:   in-memory (ephemeral — pass --postgres=...)"
            PulseBoard.PortalStore.InMemoryCustomerWorkspaceStore() :> _
        let provToken = envOr "PULSE_PROVISIONER_TOKEN" (argValue "--provisioner-token=")
        let rootDom =
          envOr "PULSE_ROOT_DOMAIN" (argValue "--root-domain=")
          |> Option.defaultValue "pulseboard.cloud"
        let provBase = provUrl |> Option.defaultValue ""
        let stripeSecret = envOr "STRIPE_SECRET_KEY" (argValue "--stripe-secret=")
        let billing : PulseBoard.PortalApi.BillingDeps option =
          match stripeSecret with
          | None -> None
          | Some sk ->
            let stripeStore : PulseBoard.StripeStore.IStripeStore =
              match pgConn with
              | Some cs ->
                try
                  PulseBoard.StripeStore.ensureSchema cs
                  printfn "  StripeStore:   Postgres (schema ensured)"
                  PulseBoard.StripeStore.PgStripeStore(cs) :> _
                with ex ->
                  eprintfn "  [ERROR] failed to initialise stripe Postgres: %s" ex.Message
                  exit 2
              | None ->
                printfn "  StripeStore:   in-memory (ephemeral — pass --postgres=...)"
                PulseBoard.StripeStore.InMemoryStripeStore() :> _
            let webhookSecret = envOr "STRIPE_WEBHOOK_SECRET" (argValue "--stripe-webhook-secret=")
            let priceStarter = envOr "STRIPE_PRICE_STARTER" (argValue "--stripe-price-starter=")
            let priceStarterAnnual = envOr "STRIPE_PRICE_STARTER_ANNUAL" (argValue "--stripe-price-starter-annual=")
            let priceProMonthly = envOr "STRIPE_PRICE_PRO" (argValue "--stripe-price-pro=")
            let priceProAnnual = envOr "STRIPE_PRICE_PRO_ANNUAL" (argValue "--stripe-price-pro-annual=")
            printfn "  Stripe:        enabled (webhook=%s, starter=%s, pro=%s)"
              (if webhookSecret.IsSome then "set" else "<unset>")
              (if priceStarter.IsSome then "set" else "<unset>")
              (if priceProMonthly.IsSome then "set" else "<unset>")
            Some
              { stripe =
                  { secretKey = sk
                    webhookSecret = webhookSecret
                    publicBase = authCfg.publicBase
                    priceStarter = priceStarter
                    priceStarterAnnual = priceStarterAnnual
                    priceProMonthly = priceProMonthly
                    priceProAnnual = priceProAnnual }
                stripeStore = stripeStore }
        Some
          { auth = authCfg
            store = workspaceStore
            provisioner =
              { baseUrl = provBase
                token = provToken
                rootDomain = rootDom }
            billing = billing }

    portal
    |> Option.iter (fun (p : PulseBoard.PortalApi.PortalApiConfig) ->
      let parseInt (s : string) =
        match Int32.TryParse s with
        | true, n -> Some n
        | _ -> None
      let sleepDays =
        envOr "PULSE_FREE_SLEEP_DAYS" (argValue "--free-sleep-days=")
        |> Option.bind parseInt
        |> Option.defaultValue 7
      let sweepMinutes =
        envOr "PULSE_FREE_SLEEP_INTERVAL_MIN" (argValue "--free-sleep-interval-min=")
        |> Option.bind parseInt
        |> Option.defaultValue 60
      let maxPerTick =
        envOr "PULSE_FREE_SLEEP_MAX_PER_TICK" (argValue "--free-sleep-max-per-tick=")
        |> Option.bind parseInt
        |> Option.defaultValue 50
      match p.provisioner.token with
      | None -> printfn "  Sleeper:       disabled (no provisioner token)"
      | Some tok when sleepDays <= 0 ->
        ignore tok
        printfn "  Sleeper:       disabled (--free-sleep-days=0)"
      | Some tok ->
        PulseBoard.FreeTierSleeper.start
          { store = p.store
            provisionerUrl = p.provisioner.baseUrl
            provisionerToken = tok
            idleThreshold = TimeSpan.FromDays (float sleepDays)
            interval = TimeSpan.FromMinutes (float sweepMinutes)
            maxPerTick = maxPerTick }
        |> ignore)

    portal
    |> Option.iter (fun (p : PulseBoard.PortalApi.PortalApiConfig) ->
      let parseInt (s : string) =
        match Int32.TryParse s with
        | true, n -> Some n
        | _ -> None
      let purgeDays =
        envOr "PULSE_PURGE_DAYS" (argValue "--purge-days=")
        |> Option.bind parseInt
        |> Option.defaultValue 30
      let overdueDays =
        envOr "PULSE_OVERDUE_GRACE_DAYS" (argValue "--overdue-grace-days=")
        |> Option.bind parseInt
        |> Option.defaultValue 3
      let intervalMin =
        envOr "PULSE_PURGE_INTERVAL_MIN" (argValue "--purge-interval-min=")
        |> Option.bind parseInt
        |> Option.defaultValue 360
      let maxPerTick =
        envOr "PULSE_PURGE_MAX_PER_TICK" (argValue "--purge-max-per-tick=")
        |> Option.bind parseInt
        |> Option.defaultValue 20
      match p.provisioner.token with
      | None -> printfn "  PurgeCron:     disabled (no provisioner token)"
      | Some _ when purgeDays <= 0 && overdueDays <= 0 ->
        printfn "  PurgeCron:     disabled (purge-days=0 and overdue-grace-days=0)"
      | Some tok ->
        PulseBoard.PurgeCron.start
          { store = p.store
            provisionerUrl = p.provisioner.baseUrl
            provisionerToken = tok
            purgeThreshold = TimeSpan.FromDays (float purgeDays)
            overdueGrace = TimeSpan.FromDays (float overdueDays)
            interval = TimeSpan.FromMinutes (float intervalMin)
            maxPerTick = maxPerTick }
        |> ignore)

    PulseBoard.SiteOnly.run port wwwroot provUrl customerAuth portal
    0

  elif argv |> Array.exists (fun a -> a = "--mode=provisioner") then
    let rootDomain =
      envOr "PULSE_ROOT_DOMAIN" (argValue "--root-domain=")
      |> Option.defaultValue "pulseboard.cloud"
    let flyToken = envOr "FLY_API_TOKEN" (argValue "--fly-token=")
    let flyOrg = envOr "FLY_ORG_SLUG" (argValue "--fly-org=")
    let dryRun = argv |> Array.contains "--dry-run"
    let image =
      envOr "PULSE_WORKSPACE_IMAGE" (argValue "--workspace-image=")
      |> Option.defaultValue "registry.fly.io/pulseboard1:latest"
    let region =
      envOr "PULSE_FLY_REGION" (argValue "--fly-region=")
      |> Option.defaultValue "iad"
    let workspaceMemMb =
      envOr "PULSE_WORKSPACE_MEM_MB" (argValue "--workspace-mem-mb=")
      |> Option.bind (fun s ->
        match Int32.TryParse s with
        | true, n when n > 0 -> Some n
        | _ -> None)
      |> Option.defaultValue 2048
    let workspaceCpus =
      envOr "PULSE_WORKSPACE_CPUS" (argValue "--workspace-cpus=")
      |> Option.bind (fun s ->
        match Int32.TryParse s with
        | true, n when n > 0 -> Some n
        | _ -> None)
      |> Option.defaultValue 1
    let provPgConn = envOr "PULSE_POSTGRES" (argValue "--postgres=")
    let provPublicUrl = envOr "PULSE_PROVISIONER_PUBLIC_URL" (argValue "--provisioner-public-url=")
    let provHeartbeats : PulseBoard.Provisioner.IHeartbeatStore =
      match provPgConn with
      | Some cs -> PulseBoard.PgWorkspaceRegistry.PgHeartbeatStore(cs) :> _
      | None -> PulseBoard.Provisioner.InMemoryHeartbeatStore() :> _
    let fly : PulseBoard.Provisioner.IFlyClient =
      if dryRun then PulseBoard.Provisioner.DryRunFlyClient() :> _
      else
        match flyToken, flyOrg with
        | Some t, Some o -> new PulseBoard.Provisioner.HttpFlyClient(t, o, provPgConn) :> _
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
      { fly = fly
        dryRun = dryRun
        registry = provRegistry
        rootDomain = rootDomain.ToLowerInvariant()
        machineConfig =
          { image = image
            region = region
            envExtra =
              Map.ofList [
                "DOTNET_GCHeapHardLimit",
                sprintf "0x%x" (int64 workspaceMemMb * 1024L * 1024L / 2L)
              ]
            sizeCpus = workspaceCpus
            sizeMemMb = workspaceMemMb }
        adminTokens =
          let raw =
            envOr "PULSE_ADMIN_TOKENS" (argValue "--admin-tokens=")
            |> Option.defaultValue ""
          raw.Split([| ','; ';'; ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
          |> Array.map (fun s -> s.Trim())
          |> Array.filter (fun s -> s.Length > 0)
          |> Set.ofArray
        postgresConn = provPgConn
        heartbeats = provHeartbeats
        provisionerPublicUrl = provPublicUrl
        workspaceBootstrapToken = envOr "PULSE_BOOTSTRAP_TOKEN" (argValue "--bootstrap-token=")
        apexPublicUrl = envOr "PULSE_APEX_PUBLIC_URL" (argValue "--apex-public-url=")
        apexHeartbeatToken =
          (envOr "PULSE_APEX_HEARTBEAT_TOKEN" (argValue "--apex-heartbeat-token="))
          |> Option.orElseWith (fun () ->
            envOr "PULSE_PROVISIONER_TOKEN" (argValue "--provisioner-token="))
        adminOidc =
          let issuer = envOr "PULSE_ADMIN_OIDC_ISSUER" (argValue "--admin-oidc-issuer=")
          let clientId = envOr "PULSE_ADMIN_OIDC_CLIENT_ID" (argValue "--admin-oidc-client-id=")
          let clientSecret = envOr "PULSE_ADMIN_OIDC_CLIENT_SECRET" (argValue "--admin-oidc-client-secret=")
          let redirectUri = envOr "PULSE_ADMIN_OIDC_REDIRECT_URI" (argValue "--admin-oidc-redirect-uri=")
          let splitCsv (raw : string) =
            raw.Split([| ','; ';'; ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun s -> s.Trim().ToLowerInvariant())
            |> Array.filter (fun s -> s.Length > 0)
            |> Set.ofArray
          let emails =
            envOr "PULSE_ADMIN_OIDC_ALLOWED_EMAILS" (argValue "--admin-oidc-allowed-emails=")
            |> Option.defaultValue "" |> splitCsv
          let domains =
            envOr "PULSE_ADMIN_OIDC_ALLOWED_DOMAINS" (argValue "--admin-oidc-allowed-domains=")
            |> Option.defaultValue "" |> splitCsv
          let sessionSecret =
            envOr "PULSE_ADMIN_OIDC_SESSION_SECRET" (argValue "--admin-oidc-session-secret=")
          match issuer, clientId, redirectUri with
          | Some iss, Some cid, Some ruri when (not (Set.isEmpty emails)) || (not (Set.isEmpty domains)) ->
            let key =
              match sessionSecret with
              | Some s ->
                try keyFromBase64 s
                with ex ->
                  eprintfn "  [WARN] PULSE_ADMIN_OIDC_SESSION_SECRET invalid (%s); generating ephemeral key" ex.Message
                  generateKey ()
              | None -> generateKey ()
            let secure = ruri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            Some
              ({ issuer = iss.TrimEnd '/'
                 clientId = cid
                 clientSecret = clientSecret
                 redirectUri = ruri
                 allowedEmails = emails
                 allowedDomains = domains
                 sessionKey = key
                 sessionTtl = TimeSpan.FromHours 12.0
                 cookieSecure = secure } : PulseBoard.AdminOidc.Config)
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
        | Some oc -> sprintf "OIDC via %s (%d email(s), %d domain(s))" oc.issuer (Set.count oc.allowedEmails) (Set.count oc.allowedDomains)
      printfn "  Admin:       enabled — %s, %s" bearerInfo oidcInfo
    match provPublicUrl with
    | Some url -> printfn "  Heartbeats:  workspaces ping %s/provision/heartbeat (PULSE_PROVISIONER_URL injected)" url
    | None -> printfn "  Heartbeats:  PULSE_PROVISIONER_PUBLIC_URL not set; workspaces won't heartbeat (admin portal will show 'never')"
    PulseBoard.Provisioner.run port cfg
    0

  else
    eprintfn "  [ERROR] PulseBoard.Cloud only supports --site-only or --mode=provisioner"
    2
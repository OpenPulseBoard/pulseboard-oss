module PulseBoard.Provisioner

open System
open System.Collections.Concurrent
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Npgsql
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open Suave.ServerErrors

// Phase 9 — per-customer workspace provisioner.
//
// The marketing site (`pulseboard.cloud`, run with `--site-only`) forwards
// signup POSTs to this service. The provisioner:
//   1. allocates a slug (the user's choice, possibly suffixed for uniqueness),
//   2. spawns a Fly Machine running the PulseBoard binary in
//      `--multi-tenant` mode for that customer,
//   3. records the slug → upstream mapping in a registry,
//   4. issues a bootstrap API key by calling /api/signup on the new
//      workspace once it's healthy,
//   5. returns `{url, apiKey, tenantId}` to the marketing site.
//
// Caddy on-demand TLS sits in front of every `*.pulseboard.cloud` host. It
// asks `GET /provision/ask?domain=<host>` whether to mint a cert (200 if
// known, 404 otherwise) and `GET /provision/route?domain=<host>` for the
// upstream URL to reverse_proxy to.
//
// The Fly client has a real HTTP implementation against api.machines.dev
// and a dry-run impl that just logs the intent and returns a synthetic
// hostname — that lets us smoke-test the flow end-to-end without
// credentials.

// -- types ------------------------------------------------------------------

type WorkspaceRecord =
  { slug         : string
    flyAppName   : string
    upstreamUrl  : string   // what Caddy / the router should proxy to
    tenantId     : string option
    apiKeyId     : string option
    ownerEmail   : string
    createdAt    : DateTimeOffset }

type IWorkspaceRegistry =
  abstract Insert : WorkspaceRecord -> unit
  abstract TryGetBySlug : string -> WorkspaceRecord option
  abstract TryGetByHost : string -> WorkspaceRecord option
  abstract Update : string -> (WorkspaceRecord -> WorkspaceRecord) -> unit

type InMemoryWorkspaceRegistry () =
  let bySlug = ConcurrentDictionary<string, WorkspaceRecord>()
  interface IWorkspaceRegistry with
    member _.Insert r = bySlug.[r.slug] <- r
    member _.TryGetBySlug s =
      match bySlug.TryGetValue s with true, r -> Some r | _ -> None
    member _.TryGetByHost h =
      // host is like "<slug>.pulseboard.cloud" — strip the apex if present.
      let s =
        let idx = h.IndexOf '.'
        if idx > 0 then h.Substring(0, idx).ToLowerInvariant()
        else h.ToLowerInvariant()
      match bySlug.TryGetValue s with true, r -> Some r | _ -> None
    member _.Update s f =
      bySlug.AddOrUpdate(s, (fun _ -> failwithf "no such slug: %s" s),
                             (fun _ old -> f old)) |> ignore

// -- Fly Machines client ----------------------------------------------------

type FlyMachineConfig =
  { image       : string                  // e.g. registry.fly.io/pulseboard1:latest
    region      : string                  // e.g. "iad"
    envExtra    : Map<string, string>     // merged with built-in defaults
    sizeCpus    : int                     // shared CPUs
    sizeMemMb   : int }

type ProvisionedWorkspace =
  { appName     : string
    publicUrl   : string                  // http://<app>.flycast — the upstream Caddy will proxy to
    internalUrl : string }                // http://<app>.flycast — same (kept for symmetry)

type IFlyClient =
  abstract CreateWorkspace : slug:string * ownerEmail:string * cfg:FlyMachineConfig -> Async<ProvisionedWorkspace>

type DryRunFlyClient () =
  interface IFlyClient with
    member _.CreateWorkspace (slug, email, cfg) = async {
      // Simulate latency so the proxy path is exercised.
      do! Async.Sleep 50
      eprintfn "  [provisioner/dry-run] would create Fly app slug=%s email=%s image=%s region=%s cpus=%d mem=%d"
        slug email cfg.image cfg.region cfg.sizeCpus cfg.sizeMemMb
      return
        { appName     = sprintf "pb-%s" slug
          publicUrl   = sprintf "http://pb-%s.flycast:80" slug
          internalUrl = sprintf "http://pb-%s.flycast:80" slug }
    }

/// Real HTTP client against the Fly Machines REST API (api.machines.dev/v1).
/// Configurable via `FLY_API_TOKEN`, `FLY_ORG_SLUG`. Each provision creates
/// a fresh app (`pb-<slug>`) and one Machine running the configured image
/// in `--multi-tenant` mode. The Machine exposes port 8080 on Fly's
/// private flycast network only — no public IPs are allocated, since all
/// external traffic is fronted by `pulseboard-caddy` which reverse-proxies
/// over flycast.
///
/// `pgAdminConn` is the provisioner's own `PULSE_POSTGRES` (the
/// connection string Fly's `postgres attach` injected). When set, each
/// new workspace gets a schema `pb_<slug>` carved out of that shared
/// database, and we inject `PULSE_POSTGRES=<same conn>;Search Path=pb_<slug>`
/// into the Machine env so the workspace's PgTenantStore / PgAuditLog /
/// PgQuotaOverrides / PgRetentionOverrides land their tables inside that
/// schema. When `None`, the workspace falls back to its in-memory store.
type HttpFlyClient (token : string, orgSlug : string, pgAdminConn : string option) =
  let http = new HttpClient(BaseAddress = Uri "https://api.machines.dev/")
  do http.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", token)

  // The Machines REST API (api.machines.dev) doesn't expose IP allocation,
  // so we need a second client pointed at the older GraphQL endpoint at
  // api.fly.io to allocate the private-v6 (flycast) address. Same token.
  let graphql = new HttpClient(BaseAddress = Uri "https://api.fly.io/")
  do graphql.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", token)

  let postJson (path : string) (body : string) = async {
    use content = new StringContent(body, Encoding.UTF8, "application/json")
    let! resp = http.PostAsync(path, content) |> Async.AwaitTask
    let! txt = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
    if not resp.IsSuccessStatusCode then
      failwithf "fly API %s -> %d: %s" path (int resp.StatusCode) txt
    return txt
  }

  /// Allocate a private-v6 (flycast) address on the given app. Without
  /// this, `<app>.flycast` does not resolve and nothing on the 6PN can
  /// reach the workspace. The Machines REST API doesn't expose IP
  /// allocation, so we have to call the GraphQL API for this one step.
  let allocateFlycast (appName : string) = async {
    let query =
      "mutation($appId:ID!){allocateIpAddress(input:{appId:$appId,type:private_v6}){ipAddress{address type}}}"
    let body =
      sprintf """{"query":%s,"variables":{"appId":%s}}"""
        (JsonSerializer.Serialize query)
        (JsonSerializer.Serialize appName)
    use content = new StringContent(body, Encoding.UTF8, "application/json")
    let! resp = graphql.PostAsync("/graphql", content) |> Async.AwaitTask
    let! txt = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
    if not resp.IsSuccessStatusCode then
      failwithf "fly GraphQL allocateIpAddress -> %d: %s" (int resp.StatusCode) txt
    // GraphQL returns 200 even on errors; surface them.
    use doc = JsonDocument.Parse txt
    match doc.RootElement.TryGetProperty "errors" with
    | true, errs when errs.ValueKind = JsonValueKind.Array && errs.GetArrayLength() > 0 ->
      failwithf "fly GraphQL allocateIpAddress errors: %s" txt
    | _ -> ()
  }

  /// Carve a Postgres schema `pb_<slug>` out of the shared cluster and
  /// return a connection string for the workspace whose `search_path`
  /// points there. Idempotent — re-running on an existing schema is a
  /// no-op. Slug has already been regex-validated upstream
  /// (^[a-z][a-z0-9-]{2,31}$); we still quote the identifier as
  /// defence-in-depth. Hyphens are illegal in unquoted Postgres
  /// identifiers, so we map `-` → `_` (slug `acme-corp` → `pb_acme_corp`).
  let provisionWorkspaceDb (slug : string) : string option =
    match pgAdminConn with
    | None -> None
    | Some adminCs ->
      let schema = "pb_" + slug.Replace('-', '_')
      use conn = new NpgsqlConnection(adminCs)
      conn.Open()
      use cmd =
        new NpgsqlCommand(
          sprintf "CREATE SCHEMA IF NOT EXISTS \"%s\"" (schema.Replace("\"", "\"\"")),
          conn)
      cmd.ExecuteNonQuery() |> ignore
      let csb = NpgsqlConnectionStringBuilder(adminCs)
      csb.SearchPath <- schema
      Some csb.ConnectionString

  interface IFlyClient with
    member _.CreateWorkspace (slug, email, cfg) = async {
      let appName = sprintf "pb-%s" slug
      // 1. Create the app.
      let appBody = sprintf """{"app_name":%s,"org_slug":%s}"""
                      (JsonSerializer.Serialize appName)
                      (JsonSerializer.Serialize orgSlug)
      let! _ = postJson "/v1/apps" appBody
      // 2. Allocate a flycast (private-v6) address so `<app>.flycast`
      //    resolves on the 6PN. Must happen before the Machine starts
      //    accepting flycast traffic.
      do! allocateFlycast appName
      // 3. Carve a Postgres schema for this workspace (if the
      //    provisioner has a Postgres connection at all). The workspace
      //    will pick up the resulting conn string from PULSE_POSTGRES
      //    and create its tenant/key/audit tables inside `pb_<slug>`.
      let workspacePgConn = provisionWorkspaceDb slug
      // 4. Create one Machine with our binary.
      //    `init.cmd` overrides the Dockerfile CMD (the ENTRYPOINT is
      //    `dotnet PulseBoard.dll`); we need `--multi-tenant` so the
      //    /api/signup endpoint is mounted and our bootstrap POST works.
      let envMap =
        let m =
          cfg.envExtra
          |> Map.add "PULSE_OWNER_EMAIL" email
          |> Map.add "PULSE_SLUG" slug
        match workspacePgConn with
        | Some cs -> m |> Map.add "PULSE_POSTGRES" cs
        | None    -> m
      let envJson =
        envMap
        |> Map.toSeq
        |> Seq.map (fun (k, v) ->
             sprintf "%s:%s"
               (JsonSerializer.Serialize k)
               (JsonSerializer.Serialize v))
        |> String.concat ","
      let machineBody =
        sprintf """{"name":%s,"region":%s,"config":{"image":%s,"env":{%s},"init":{"cmd":["--multi-tenant"]},"services":[{"protocol":"tcp","internal_port":8080,"ports":[{"port":80,"handlers":["http"]}]}],"guest":{"cpu_kind":"shared","cpus":%d,"memory_mb":%d}}}"""
          (JsonSerializer.Serialize (appName + "-0"))
          (JsonSerializer.Serialize cfg.region)
          (JsonSerializer.Serialize cfg.image)
          envJson
          cfg.sizeCpus
          cfg.sizeMemMb
      let! _ = postJson (sprintf "/v1/apps/%s/machines" appName) machineBody
      // Caddy's dynamic-upstream module doesn't infer the port from the
      // scheme — give it host:port explicitly. Without the `:80`, Caddy
      // dials port 0 and times out (see
      // `dial tcp [...]:0: i/o timeout` in fly logs).
      return
        { appName     = appName
          publicUrl   = sprintf "http://%s.flycast:80" appName
          internalUrl = sprintf "http://%s.flycast:80" appName }
    }

  interface IDisposable with
    member _.Dispose () =
      http.Dispose ()
      graphql.Dispose ()

// -- bootstrap the new workspace -------------------------------------------
//
// After Fly says the Machine is up, we still need to mint the first API
// key on that workspace. We do that by calling its own /api/signup once.
// The workspace must be running in --multi-tenant mode (which our spawn
// config sets via PULSE_MULTI_TENANT). The bootstrap call is just a
// regular POST; the workspace has no prior keys so it answers normally.
//
// TODO: harden this. A real implementation would (a) gate /api/signup
// behind a single-use bootstrap token passed via env at machine create,
// and (b) flip a "bootstrapped=true" bit so subsequent /api/signup calls
// require operator action. For first cut we accept the small window
// between machine boot and our own POST.

type BootstrapResult =
  { tenantId : string
    apiKeyId : string
    apiKey   : string }

/// Poll the new workspace's /healthz until it returns 200 (or we give up).
/// flycast DNS resolves as soon as the IP is allocated, but the Machine
/// itself takes time to: pull the image (cold), start the container,
/// boot the .NET runtime, and bind its sockets. Meanwhile Fly's flycast
/// proxy will accept TCP connections and hold them open waiting for the
/// container to become reachable — so we use a short per-request timeout
/// (so a stalled probe doesn't burn the whole budget) and a long total
/// deadline (so cold image pulls have room).
let private waitForHealth (http : HttpClient) (baseUrl : string) : Async<unit> = async {
  let url = baseUrl.TrimEnd('/') + "/healthz"
  let deadline = DateTime.UtcNow.AddMinutes 3.0
  let mutable lastErr = "(no attempts yet)"
  let mutable ok = false
  while not ok && DateTime.UtcNow < deadline do
    try
      use cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds 5.0)
      let! resp = http.GetAsync(url, cts.Token) |> Async.AwaitTask
      if resp.IsSuccessStatusCode then ok <- true
      else lastErr <- sprintf "HTTP %d" (int resp.StatusCode)
    with ex ->
      lastErr <- ex.Message
    if not ok then do! Async.Sleep 1000
  if not ok then
    failwithf "workspace %s never became healthy: %s" url lastErr
}

let private bootstrapWorkspace (http : HttpClient) (baseUrl : string)
                               (slug : string) (email : string)
                               : Async<BootstrapResult> = async {
  do! waitForHealth http baseUrl
  let body = sprintf """{"slug":%s,"email":%s}"""
               (JsonSerializer.Serialize slug)
               (JsonSerializer.Serialize email)
  use content = new StringContent(body, Encoding.UTF8, "application/json")
  let url = baseUrl.TrimEnd('/') + "/api/signup"
  let! resp = http.PostAsync(url, content) |> Async.AwaitTask
  let! txt = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
  if not resp.IsSuccessStatusCode then
    failwithf "workspace bootstrap %s -> %d: %s" url (int resp.StatusCode) txt
  use doc = JsonDocument.Parse txt
  let r = doc.RootElement
  let g (name : string) = r.GetProperty(name).GetString()
  return { tenantId = g "tenantId"; apiKeyId = g "apiKeyId"; apiKey = g "apiKey" }
}

// In dry-run mode we mint a synthetic key so the marketing flow can
// display something coherent in tests without actually reaching a Fly
// Machine that doesn't exist.
let private bootstrapDryRun (slug : string) : BootstrapResult =
  let rnd = Guid.NewGuid().ToString("N").Substring(0, 12)
  { tenantId = sprintf "tenant_%s" rnd
    apiKeyId = sprintf "key_%s" (rnd.Substring(0, 8))
    apiKey   = sprintf "pk_%s.%s" rnd (Guid.NewGuid().ToString("N")) }

// -- config + state ---------------------------------------------------------

type ProvisionerConfig =
  { fly           : IFlyClient
    dryRun        : bool         // when true, skip workspace bootstrap HTTP call
    registry      : IWorkspaceRegistry
    rootDomain    : string       // e.g. "pulseboard.cloud"
    machineConfig : FlyMachineConfig }

// -- HTTP surface -----------------------------------------------------------

let private readBody (req : HttpRequest) =
  if isNull req.rawForm || req.rawForm.Length = 0 then ""
  else Encoding.UTF8.GetString req.rawForm

let private jsonResp (status : int) (body : string) : WebPart =
  let writer =
    match status with
    | 200 -> OK | 201 -> Suave.Successful.CREATED
    | 400 -> BAD_REQUEST | 404 -> NOT_FOUND | 409 -> Suave.RequestErrors.CONFLICT
    | _   -> INTERNAL_ERROR
  writer body >=> Writers.setMimeType "application/json"

let private errJson status msg =
  jsonResp status (sprintf """{"error":%s}""" (JsonSerializer.Serialize (msg : string)))

let private slugOk (s : string) =
  let re = System.Text.RegularExpressions.Regex(@"^[a-z][a-z0-9-]{2,31}$")
  re.IsMatch s && not (s.StartsWith "-") && not (s.EndsWith "-")

/// POST /api/provision  body: {"slug":"acme","email":"a@b.co"}
let private provision (cfg : ProvisionerConfig) (httpForBootstrap : HttpClient) : WebPart =
  fun ctx -> async {
    try
      let body = readBody ctx.request
      use doc = JsonDocument.Parse (if String.IsNullOrWhiteSpace body then "{}" else body)
      let root = doc.RootElement
      let tryStr (n : string) =
        match root.TryGetProperty n with
        | true, v when v.ValueKind = JsonValueKind.String ->
          let s = v.GetString().Trim() in if s = "" then None else Some s
        | _ -> None
      match tryStr "slug", tryStr "email" with
      | None, _ -> return! errJson 400 "field 'slug' is required" ctx
      | _, None -> return! errJson 400 "field 'email' is required" ctx
      | Some rawSlug, Some email ->
        let slug = rawSlug.ToLowerInvariant()
        if not (slugOk slug) then
          return! errJson 400 "slug must match ^[a-z][a-z0-9-]{2,31}$ and not start/end with '-'" ctx
        else
          // Append a 4-hex suffix on collision so retries succeed.
          let finalSlug =
            match cfg.registry.TryGetBySlug slug with
            | None -> slug
            | Some _ ->
              let salt = Guid.NewGuid().ToString("N").Substring(0, 4)
              sprintf "%s-%s" slug salt
          let! ws = cfg.fly.CreateWorkspace(finalSlug, email, cfg.machineConfig)
          // Record before bootstrap so /provision/ask works as soon as
          // Caddy receives the first cert request.
          let host = sprintf "%s.%s" finalSlug cfg.rootDomain
          let record0 =
            { slug = finalSlug; flyAppName = ws.appName
              upstreamUrl = ws.publicUrl
              tenantId = None; apiKeyId = None
              ownerEmail = email; createdAt = DateTimeOffset.UtcNow }
          cfg.registry.Insert record0
          // Bootstrap the workspace key. In dry-run we synthesise one.
          let! boot =
            if cfg.dryRun then async { return bootstrapDryRun finalSlug }
            else bootstrapWorkspace httpForBootstrap ws.publicUrl finalSlug email
          cfg.registry.Update finalSlug (fun r ->
            { r with tenantId = Some boot.tenantId; apiKeyId = Some boot.apiKeyId })
          let publicHostUrl = sprintf "https://%s" host
          let resp =
            sprintf """{"slug":%s,"url":%s,"tenantId":%s,"apiKey":%s,"apiKeyId":%s}"""
              (JsonSerializer.Serialize finalSlug)
              (JsonSerializer.Serialize publicHostUrl)
              (JsonSerializer.Serialize boot.tenantId)
              (JsonSerializer.Serialize boot.apiKey)
              (JsonSerializer.Serialize boot.apiKeyId)
          return! jsonResp 201 resp ctx
    with ex ->
      eprintfn "  [provisioner] %s" ex.Message
      return! errJson 500 ex.Message ctx
  }

let private askOrRoute (cfg : ProvisionerConfig) (isAsk : bool) : WebPart =
  fun ctx -> async {
    let host =
      match ctx.request.queryParam "domain" with
      | Choice1Of2 v -> v.Trim().ToLowerInvariant()
      | _ -> ""
    if host = "" then return! errJson 400 "missing ?domain=" ctx
    elif host = cfg.rootDomain then
      // Apex host — Caddy may legitimately ask about it (when site-only is
      // also fronted by the same Caddy). Answer "yes" so it gets a cert.
      if isAsk then return! OK "" ctx
      else return! jsonResp 200 """{"upstream":"http://localhost:8080"}""" ctx
    else
      match cfg.registry.TryGetByHost host with
      | None -> return! errJson 404 (sprintf "unknown host: %s" host) ctx
      | Some r ->
        if isAsk then return! OK "" ctx
        else
          let body = sprintf """{"upstream":%s}""" (JsonSerializer.Serialize r.upstreamUrl)
          return! jsonResp 200 body ctx
  }

let webPart (cfg : ProvisionerConfig) : WebPart =
  // One shared HttpClient for the workspace bootstrap call. Created once
  // here so we don't churn sockets on every signup. Total timeout is
  // generous because /healthz polling uses its own per-request
  // CancellationToken; the bootstrap POST itself is fast once /healthz
  // is green.
  let http = new HttpClient(Timeout = TimeSpan.FromMinutes 5.0)
  choose [
    GET >=> path "/healthz" >=>
      (OK """{"status":"ok","role":"provisioner"}"""
       >=> Writers.setMimeType "application/json")
    POST >=> path "/api/provision" >=> provision cfg http
    GET  >=> path "/provision/ask"   >=> askOrRoute cfg true
    GET  >=> path "/provision/route" >=> askOrRoute cfg false
  ]

/// Run a standalone provisioner service. Returns once the server exits.
let run (port : int) (cfg : ProvisionerConfig) : unit =
  // Accepts a comma-separated list, e.g. PULSE_BIND_ADDR="::,0.0.0.0".
  // See the matching block in Program.fs / SiteOnly.fs for the rationale
  // (.NET on Linux defaults AF_INET6 sockets to IPV6_V6ONLY=1, so a
  // single `::` listener does NOT accept the IPv4 loopback that Fly's
  // health check uses; we need both an IPv6 and an IPv4 binding to be
  // truly dual-stack).
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
         | []  ->
           eprintfn "  [WARN] PULSE_BIND_ADDR=%s yielded no valid IPs; falling back to 127.0.0.1" s
           [ IPAddress.Loopback ]
         | ips -> ips
  let config =
    { defaultConfig with
        bindings = bindAddrs |> List.map (fun ip -> HttpBinding.create HTTP ip (uint16 port)) }
  for ip in bindAddrs do
    printfn "PulseBoard (provisioner) listening on http://%O:%d" ip port
  printfn "  Root domain: %s" cfg.rootDomain
  printfn "  Fly client:  %s" (if cfg.dryRun then "dry-run (no API calls)" else "live (api.machines.dev)")
  printfn "  Image:       %s (region %s)" cfg.machineConfig.image cfg.machineConfig.region
  startWebServer config (webPart cfg)

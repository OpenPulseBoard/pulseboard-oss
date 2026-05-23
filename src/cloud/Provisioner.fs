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

/// Latest heartbeat ping from a workspace. Workspaces (running in
/// `--multi-tenant` mode with `PULSE_SLUG` and `PULSE_PROVISIONER_URL`
/// set in their env) POST `/provision/heartbeat` every ~15s. The admin
/// portal uses this to surface live/stale state independently of the
/// Fly Machines API (which only tells us the machine is *running*, not
/// that the PulseBoard process inside it is healthy).
type Heartbeat =
  { slug       : string
    lastSeenAt : DateTimeOffset
    version    : string option }

type IHeartbeatStore =
  /// Stamp `lastSeenAt = at` and replace `version` for this slug.
  /// Inserts a new row if missing. No-op on missing-slug semantics:
  /// the caller has already validated the slug exists in the registry.
  abstract Record : slug:string -> version:string option -> at:DateTimeOffset -> unit
  abstract TryGet : slug:string -> Heartbeat option
  /// Snapshot of every slug → latest heartbeat. Used by the admin
  /// portal listing so we don't issue one query per row.
  abstract All : unit -> Map<string, Heartbeat>

type InMemoryHeartbeatStore () =
  let m = ConcurrentDictionary<string, Heartbeat>()
  interface IHeartbeatStore with
    member _.Record slug version at =
      m.[slug] <- { slug = slug; lastSeenAt = at; version = version }
    member _.TryGet slug =
      match m.TryGetValue slug with true, h -> Some h | _ -> None
    member _.All () =
      m
      |> Seq.map (fun kv -> kv.Key, kv.Value)
      |> Map.ofSeq

type WorkspaceRecord =
  { slug         : string
    flyAppName   : string
    upstreamUrl  : string   // what Caddy / the router should proxy to
    tenantId     : string option
    apiKeyId     : string option
    ownerEmail   : string
    createdAt    : DateTimeOffset
    /// When `Some`, the workspace is archived: its Fly machines have
    /// been stopped, the router refuses traffic (`410 Gone`), and the
    /// operator can either unarchive (reversible) or purge (irreversible
    /// — destroys the Fly app and drops the Postgres schema). When
    /// `None`, the workspace is live.
    archivedAt   : DateTimeOffset option }

type IWorkspaceRegistry =
  abstract Insert : WorkspaceRecord -> unit
  abstract TryGetBySlug : string -> WorkspaceRecord option
  abstract TryGetByHost : string -> WorkspaceRecord option
  abstract Update : string -> (WorkspaceRecord -> WorkspaceRecord) -> unit
  /// Return every workspace record, newest first. Used by the admin
  /// portal; pagination is intentionally not part of v1 (a few hundred
  /// rows fit comfortably in one JSON response).
  abstract List : unit -> WorkspaceRecord list
  /// Stamp / clear the `archivedAt` field. `None` brings the workspace
  /// back online; `Some now` archives it.
  abstract SetArchived : slug:string -> at:DateTimeOffset option -> unit
  /// Remove the row entirely. Called by `purgeWorkspace` after Fly app
  /// destruction; no-op if the slug is already gone.
  abstract Delete : slug:string -> unit

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
    member _.List () =
      bySlug.Values
      |> Seq.sortByDescending (fun r -> r.createdAt)
      |> List.ofSeq
    member _.SetArchived slug at =
      bySlug.AddOrUpdate(slug, (fun _ -> failwithf "no such slug: %s" slug),
                               (fun _ old -> { old with archivedAt = at })) |> ignore
    member _.Delete slug =
      bySlug.TryRemove slug |> ignore

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

/// A live Fly Machine, as reported by `GET /v1/apps/<app>/machines`.
/// Populated by `IFlyClient.ListMachines`; consumed by the admin portal
/// to surface per-workspace state without round-tripping through the
/// workspace itself.
type MachineInfo =
  { id        : string
    state     : string   // "started" | "stopped" | "created" | "destroying" | …
    region    : string
    createdAt : string } // ISO-8601 from the Fly API, kept as-is

type IFlyClient =
  abstract CreateWorkspace : slug:string * ownerEmail:string * cfg:FlyMachineConfig -> Async<ProvisionedWorkspace>
  /// List the Machines belonging to a Fly app. Empty list when the app
  /// exists but has no machines (e.g. after a manual `fly machine
  /// destroy`). Throws if the app itself is missing or the API errors.
  abstract ListMachines : appName:string -> Async<MachineInfo list>
  /// Stop every Machine on the app. Returns the number of machines
  /// acted upon. Cheap idempotent: stopping an already-stopped machine
  /// is a no-op on Fly's side.
  abstract SuspendApp : appName:string -> Async<int>
  /// Start every Machine on the app. Returns the number of machines
  /// acted upon. Cheap idempotent: starting an already-started machine
  /// is a no-op on Fly's side.
  abstract ResumeApp : appName:string -> Async<int>
  /// Delete the Fly app entirely (with `?force=true` so any remaining
  /// machines are torn down in the same call). Idempotent: a 404 from
  /// Fly (app already gone) is treated as success.
  abstract DestroyApp : appName:string -> Async<unit>

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
    member _.ListMachines appName = async {
      // Synthetic single "started" machine so the admin portal renders
      // a sensible state column in dry-run mode.
      return
        [ { id        = appName + "-0"
            state     = "started"
            region    = "iad"
            createdAt = DateTimeOffset.UtcNow.ToString("o") } ]
    }
    member _.SuspendApp appName = async {
      eprintfn "  [provisioner/dry-run] would stop all machines on %s" appName
      return 1
    }
    member _.ResumeApp appName = async {
      eprintfn "  [provisioner/dry-run] would start all machines on %s" appName
      return 1
    }
    member _.DestroyApp appName = async {
      eprintfn "  [provisioner/dry-run] would destroy Fly app %s" appName
      return ()
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

    member _.ListMachines appName = async {
      // GET /v1/apps/<app>/machines — returns a JSON array of machines.
      // We only surface the four fields the admin portal renders; the
      // full response is much larger (config, checks, events, …).
      let path = sprintf "/v1/apps/%s/machines" appName
      let! resp = http.GetAsync(path) |> Async.AwaitTask
      let! txt = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
      if not resp.IsSuccessStatusCode then
        failwithf "fly API GET %s -> %d: %s" path (int resp.StatusCode) txt
      use doc = JsonDocument.Parse txt
      let root = doc.RootElement
      if root.ValueKind <> JsonValueKind.Array then
        return []
      else
        let getStr (el : JsonElement) (n : string) =
          match el.TryGetProperty n with
          | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
          | _ -> ""
        let items =
          root.EnumerateArray()
          |> Seq.map (fun el ->
               { id        = getStr el "id"
                 state     = getStr el "state"
                 region    = getStr el "region"
                 createdAt = getStr el "created_at" })
          |> List.ofSeq
        return items
    }

    member this.SuspendApp appName = async {
      // Fly Machines API: POST /v1/apps/<app>/machines/<id>/stop with
      // empty JSON body. Already-stopped machines return 200.
      let! machines = (this :> IFlyClient).ListMachines appName
      for m in machines do
        let path = sprintf "/v1/apps/%s/machines/%s/stop" appName m.id
        let! _ = postJson path "{}"
        ()
      return machines.Length
    }

    member this.ResumeApp appName = async {
      // Fly Machines API: POST /v1/apps/<app>/machines/<id>/start.
      let! machines = (this :> IFlyClient).ListMachines appName
      for m in machines do
        let path = sprintf "/v1/apps/%s/machines/%s/start" appName m.id
        let! _ = postJson path "{}"
        ()
      return machines.Length
    }

    member _.DestroyApp appName = async {
      // Fly Machines API: DELETE /v1/apps/<app>?force=true tears down
      // the app and its machines in one call. A 404 means the app is
      // already gone — treat as success for idempotency.
      let path = sprintf "/v1/apps/%s?force=true" appName
      let! resp = http.DeleteAsync(path) |> Async.AwaitTask
      let! txt = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
      if resp.StatusCode = System.Net.HttpStatusCode.NotFound then
        eprintfn "  [provisioner] DestroyApp %s: app already gone (404)" appName
      elif not resp.IsSuccessStatusCode then
        failwithf "fly API DELETE %s -> %d: %s" path (int resp.StatusCode) txt
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
    machineConfig : FlyMachineConfig
    /// Bearer tokens accepted on `/admin/*` routes. Empty set ⇒ the
    /// admin portal is disabled (every request returns 404 so the
    /// surface is invisible). Populate from `PULSE_ADMIN_TOKENS=tok1,tok2`
    /// or `--admin-tokens=…`. Tokens are compared in constant time.
    adminTokens   : Set<string>
    /// Optional admin Postgres connection string. When set, `purge`
    /// also issues `DROP SCHEMA pb_<slug> CASCADE` so the workspace's
    /// per-tenant tables go away with the Fly app. When `None`, purge
    /// only destroys the Fly app and the registry row (data lives in
    /// each workspace's local SQLite / in-memory store anyway).
    postgresConn  : string option
    /// Latest-heartbeat store. Workspaces ping `/provision/heartbeat`
    /// every ~15s; the admin portal joins this against the registry
    /// to render "last seen Xs ago" per row.
    heartbeats    : IHeartbeatStore
    /// Public flycast URL of the provisioner itself (e.g.
    /// `http://pulseboard-provisioner.flycast`). When `Some`, gets
    /// injected into every new workspace machine as
    /// `PULSE_PROVISIONER_URL` so the workspace knows where to ship
    /// its heartbeats. When `None`, workspaces still come up — they
    /// just won't heartbeat (admin portal shows them as never-seen).
    provisionerPublicUrl : string option
    /// OIDC config for the admin portal. When `Some`, the portal
    /// exposes `/admin/login` / `/admin/callback` / `/admin/logout`
    /// and `adminAuth` accepts a valid `pulse_admin` session cookie
    /// in addition to the bearer tokens in `adminTokens`. When `None`,
    /// the portal is bearer-only (the original behaviour).
    adminOidc            : AdminOidc.Config option }

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
          // Inject PULSE_PROVISIONER_URL so the spawned workspace knows
          // where to ship heartbeats. Operator can pre-set it in
          // machineConfig.envExtra to override (e.g. for canary).
          let machineCfg =
            match cfg.provisionerPublicUrl with
            | Some url when not (cfg.machineConfig.envExtra.ContainsKey "PULSE_PROVISIONER_URL") ->
              { cfg.machineConfig with
                  envExtra = cfg.machineConfig.envExtra |> Map.add "PULSE_PROVISIONER_URL" url }
            | _ -> cfg.machineConfig
          let! ws = cfg.fly.CreateWorkspace(finalSlug, email, machineCfg)
          // Record before bootstrap so /provision/ask works as soon as
          // Caddy receives the first cert request.
          let host = sprintf "%s.%s" finalSlug cfg.rootDomain
          let record0 =
            { slug = finalSlug; flyAppName = ws.appName
              upstreamUrl = ws.publicUrl
              tenantId = None; apiKeyId = None
              ownerEmail = email; createdAt = DateTimeOffset.UtcNow
              archivedAt = None }
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
    elif host = "admin." + cfg.rootDomain then
      // Admin portal — Caddy proxies it back to us. We answer the
      // on-demand ask so the cert is issued lazily on first request
      // (avoids the boot-time race where Caddy's eager ACME fails
      // because DNS hasn't propagated yet). `/provision/route` should
      // never be hit for this host because the Caddyfile hard-codes
      // the upstream in a named block — but return something sensible
      // just in case the block is ever removed.
      if isAsk then return! OK "" ctx
      else
        match cfg.provisionerPublicUrl with
        | Some url ->
          let body = sprintf """{"upstream":%s}""" (JsonSerializer.Serialize url)
          return! jsonResp 200 body ctx
        | None ->
          return! errJson 404 "admin host has no upstream configured" ctx
    else
      match cfg.registry.TryGetByHost host with
      | None -> return! errJson 404 (sprintf "unknown host: %s" host) ctx
      | Some r when r.archivedAt.IsSome ->
        // Archived workspaces are off-limits to live traffic. Caddy's
        // ask returns 404 (don't mint a fresh cert), route returns 410
        // Gone so the operator sees a clear signal instead of a 502.
        if isAsk then return! NOT_FOUND "archived" ctx
        else return! Suave.RequestErrors.GONE "workspace archived" ctx
      | Some r ->
        if isAsk then return! OK "" ctx
        else
          let body = sprintf """{"upstream":%s}""" (JsonSerializer.Serialize r.upstreamUrl)
          return! jsonResp 200 body ctx
  }

/// POST /provision/heartbeat — workspaces ping this every ~15s.
/// Body: `{"slug":"acme","version":"0.9.1"}` (`version` optional).
/// Returns 200 `{ok:true,intervalSec:15}` on success, 404 for unknown
/// slugs, 410 Gone for archived ones, 400 for malformed bodies.
///
/// This endpoint is intentionally UNAUTHENTICATED at the HTTP layer:
/// the provisioner listens on flycast-only (private-v6) in production,
/// so only machines inside the same Fly org can reach it. A spoofed
/// heartbeat is at worst a misleading "last seen" badge in the admin
/// portal; it can't escalate privileges or affect routing.
let private heartbeat (cfg : ProvisionerConfig) : WebPart =
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
      match tryStr "slug" with
      | None -> return! errJson 400 "field 'slug' is required" ctx
      | Some slug ->
        match cfg.registry.TryGetBySlug (slug.ToLowerInvariant()) with
        | None -> return! errJson 404 (sprintf "unknown workspace: %s" slug) ctx
        | Some r when r.archivedAt.IsSome ->
          return! Suave.RequestErrors.GONE "workspace archived" ctx
        | Some r ->
          let version = tryStr "version"
          cfg.heartbeats.Record r.slug version DateTimeOffset.UtcNow
          return! jsonResp 200 """{"ok":true,"intervalSec":15}""" ctx
    with ex ->
      return! errJson 400 ex.Message ctx
  }

// -- admin portal -----------------------------------------------------------
//
// `/admin/*` is gated by a bearer token from `PULSE_ADMIN_TOKENS`. When
// no tokens are configured, the admin surface is entirely invisible:
// every `/admin/*` request returns 404 just like any other unknown
// path. When tokens are configured, missing/invalid bearers return 401.

/// Constant-time comparison so a timing oracle can't be used to enumerate
/// valid tokens character by character.
let private ctEquals (a : string) (b : string) =
  if a.Length <> b.Length then false
  else
    let mutable diff = 0
    for i in 0 .. a.Length - 1 do diff <- diff ||| (int a.[i] ^^^ int b.[i])
    diff = 0

let private extractBearer (req : HttpRequest) =
  match req.header "authorization" with
  | Choice1Of2 v ->
    let v = v.Trim()
    if v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
    then Some (v.Substring(7).Trim())
    else None
  | _ -> None

let private adminAuth (cfg : ProvisionerConfig) (inner : WebPart) : WebPart =
  fun ctx -> async {
    // Surface is "on" if either bearer tokens or OIDC is configured.
    // When neither is set, every /admin/* returns 404 so the surface
    // is indistinguishable from a typo.
    let bearerOn = not (Set.isEmpty cfg.adminTokens)
    let oidcOn   = cfg.adminOidc.IsSome
    if not bearerOn && not oidcOn then
      return! NOT_FOUND "Not found." ctx
    else
      // Try bearer first (CI/automation), then fall back to the
      // session cookie (humans via OIDC).
      let bearerOk =
        match extractBearer ctx.request with
        | Some tok when bearerOn ->
          cfg.adminTokens |> Set.exists (fun t -> ctEquals t tok)
        | _ -> false
      let cookieOk =
        match cfg.adminOidc with
        | Some oc -> AdminOidc.tryReadSession oc ctx.request |> Option.isSome
        | None    -> false
      if bearerOk || cookieOk then return! inner ctx
      else
        // 401 with no body — clients distinguish bearer-vs-cookie at
        // call sites (the HTML portal redirects to /admin/login on 401
        // when OIDC is configured).
        return! Suave.RequestErrors.UNAUTHORIZED "authentication required" ctx
  }

/// Same visibility rule as `adminAuth`, but for surfaces the operator
/// hits unauthenticated (the HTML portal page itself). When neither
/// bearer tokens nor OIDC is configured the route returns 404. When
/// OIDC IS configured and the visitor has no valid session cookie,
/// 302 to `/admin/login` so they don't see a stale paste-your-token
/// page. Otherwise serve the supplied web part (the JSON endpoints
/// it calls do their own auth via `adminAuth`).
let private adminVisible (cfg : ProvisionerConfig) (inner : WebPart) : WebPart =
  fun ctx -> async {
    let bearerOn = not (Set.isEmpty cfg.adminTokens)
    let oidcOn   = cfg.adminOidc.IsSome
    if not bearerOn && not oidcOn then
      return! NOT_FOUND "Not found." ctx
    else
      match cfg.adminOidc with
      | Some oc when AdminOidc.tryReadSession oc ctx.request |> Option.isNone
                  && not bearerOn ->
        // OIDC-only deployment + no cookie -> kick straight to login.
        return! Suave.Redirection.FOUND "/admin/login?returnTo=/admin" ctx
      | _ ->
        return! inner ctx
  }

/// JSON-encode a workspace record plus its live Fly machines. `machines`
/// is either an array of `MachineInfo` JSON objects, or `null` if the
/// Fly API call failed (we surface the error in `machinesError` instead
/// of poisoning the whole response). Sensitive fields (apiKey) were
/// never stored, so this is safe to expose to authenticated admins.
let private machineToJson (m : MachineInfo) =
  let s = JsonSerializer.Serialize : string -> string
  sprintf """{"id":%s,"state":%s,"region":%s,"createdAt":%s}"""
    (s m.id) (s m.state) (s m.region) (s m.createdAt)

let private recordToJson (r : WorkspaceRecord) (machines : Result<MachineInfo list, string>) (hb : Heartbeat option) =
  let s = JsonSerializer.Serialize : string -> string
  let optS = function Some v -> s v | None -> "null"
  let optDate = function Some (d : DateTimeOffset) -> s (d.ToString("o")) | None -> "null"
  let machinesField, errField =
    match machines with
    | Result.Ok ms ->
      let arr = ms |> List.map machineToJson |> String.concat ","
      sprintf "[%s]" arr, "null"
    | Result.Error e -> "null", s e
  let lastSeenAt = hb |> Option.map (fun h -> h.lastSeenAt)
  let version    = hb |> Option.bind (fun h -> h.version)
  sprintf """{"slug":%s,"flyAppName":%s,"upstreamUrl":%s,"tenantId":%s,"apiKeyId":%s,"ownerEmail":%s,"createdAt":%s,"archivedAt":%s,"machines":%s,"machinesError":%s,"lastSeenAt":%s,"version":%s}"""
    (s r.slug)
    (s r.flyAppName)
    (s r.upstreamUrl)
    (optS r.tenantId)
    (optS r.apiKeyId)
    (s r.ownerEmail)
    (s (r.createdAt.ToString("o")))
    (optDate r.archivedAt)
    machinesField
    errField
    (optDate lastSeenAt)
    (optS version)

/// Bounded-concurrency fan-out: ask the Fly client about every app in
/// parallel, but at most `maxConcurrent` in flight at a time. Per-row
/// errors are captured as `Error msg` so one dead app doesn't poison the
/// whole admin listing.
let private fetchMachinesParallel (cfg : ProvisionerConfig) (rows : WorkspaceRecord list)
    : Async<Map<string, Result<MachineInfo list, string>>> = async {
  let maxConcurrent = 8
  use sem = new System.Threading.SemaphoreSlim(maxConcurrent, maxConcurrent)
  let one (r : WorkspaceRecord) = async {
    do! sem.WaitAsync() |> Async.AwaitTask
    try
      try
        let! ms = cfg.fly.ListMachines r.flyAppName
        return r.slug, Result.Ok ms
      with ex ->
        return r.slug, Result.Error ex.Message
    finally
      sem.Release() |> ignore
  }
  let! pairs = rows |> List.map one |> Async.Parallel
  return pairs |> Map.ofArray
}

/// GET /admin/workspaces — list every provisioned workspace, newest first,
/// enriched with live Fly machine state. Pass `?machines=0` to skip the
/// Fly fan-out and return the bare registry (faster, no external calls).
let private listWorkspaces (cfg : ProvisionerConfig) : WebPart =
  fun ctx -> async {
    try
      let rows = cfg.registry.List()
      let skipMachines =
        match ctx.request.queryParam "machines" with
        | Choice1Of2 v ->
          let v = v.Trim().ToLowerInvariant()
          v = "0" || v = "false" || v = "no"
        | _ -> false
      let! machinesBySlug =
        if skipMachines then async { return Map.empty }
        else fetchMachinesParallel cfg rows
      let lookup slug =
        match Map.tryFind slug machinesBySlug with
        | Some r -> r
        | None   -> Result.Ok []
      let heartbeats = cfg.heartbeats.All()
      let items =
        rows
        |> List.map (fun r -> recordToJson r (lookup r.slug) (Map.tryFind r.slug heartbeats))
        |> String.concat ","
      let body = sprintf """{"count":%d,"items":[%s]}""" rows.Length items
      return! jsonResp 200 body ctx
    with ex ->
      eprintfn "  [provisioner/admin] list failed: %s" ex.Message
      return! errJson 500 ex.Message ctx
  }

/// POST /admin/workspaces/<slug>/suspend — stop every Machine on the
/// workspace's Fly app. Returns `{slug, action:"suspend", machines:N}`.
/// 404 when the slug is unknown; 502 when the Fly API call fails.
let private suspendWorkspace (cfg : ProvisionerConfig) (slug : string) : WebPart =
  fun ctx -> async {
    match cfg.registry.TryGetBySlug slug with
    | None ->
      return! errJson 404 (sprintf "unknown workspace: %s" slug) ctx
    | Some r ->
      try
        let! n = cfg.fly.SuspendApp r.flyAppName
        let body =
          sprintf """{"slug":%s,"flyAppName":%s,"action":"suspend","machines":%d}"""
            (JsonSerializer.Serialize (slug : string))
            (JsonSerializer.Serialize (r.flyAppName : string))
            n
        return! jsonResp 200 body ctx
      with ex ->
        eprintfn "  [provisioner/admin] suspend %s failed: %s" slug ex.Message
        return! errJson 502 ex.Message ctx
  }

/// POST /admin/workspaces/<slug>/resume — start every Machine on the
/// workspace's Fly app. Same response shape as `suspendWorkspace`.
let private resumeWorkspace (cfg : ProvisionerConfig) (slug : string) : WebPart =
  fun ctx -> async {
    match cfg.registry.TryGetBySlug slug with
    | None ->
      return! errJson 404 (sprintf "unknown workspace: %s" slug) ctx
    | Some r ->
      try
        let! n = cfg.fly.ResumeApp r.flyAppName
        let body =
          sprintf """{"slug":%s,"flyAppName":%s,"action":"resume","machines":%d}"""
            (JsonSerializer.Serialize (slug : string))
            (JsonSerializer.Serialize (r.flyAppName : string))
            n
        return! jsonResp 200 body ctx
      with ex ->
        eprintfn "  [provisioner/admin] resume %s failed: %s" slug ex.Message
        return! errJson 502 ex.Message ctx
  }

/// POST /admin/workspaces/<slug>/archive — stop all machines AND mark
/// the workspace archived so the router refuses live traffic. Reversible
/// via `unarchive`. 409 when already archived (idempotency-friendly: the
/// caller can treat 409 as success).
let private archiveWorkspace (cfg : ProvisionerConfig) (slug : string) : WebPart =
  fun ctx -> async {
    match cfg.registry.TryGetBySlug slug with
    | None -> return! errJson 404 (sprintf "unknown workspace: %s" slug) ctx
    | Some r when r.archivedAt.IsSome ->
      return! errJson 409 (sprintf "already archived at %s" (r.archivedAt.Value.ToString("o"))) ctx
    | Some r ->
      try
        let! n = cfg.fly.SuspendApp r.flyAppName
        let at = DateTimeOffset.UtcNow
        cfg.registry.SetArchived slug (Some at)
        let body =
          sprintf """{"slug":%s,"flyAppName":%s,"action":"archive","machines":%d,"archivedAt":%s}"""
            (JsonSerializer.Serialize (slug : string))
            (JsonSerializer.Serialize (r.flyAppName : string))
            n
            (JsonSerializer.Serialize (at.ToString("o")))
        return! jsonResp 200 body ctx
      with ex ->
        eprintfn "  [provisioner/admin] archive %s failed: %s" slug ex.Message
        return! errJson 502 ex.Message ctx
  }

/// POST /admin/workspaces/<slug>/unarchive — clear the archived flag and
/// restart machines. 409 when the workspace was never archived.
let private unarchiveWorkspace (cfg : ProvisionerConfig) (slug : string) : WebPart =
  fun ctx -> async {
    match cfg.registry.TryGetBySlug slug with
    | None -> return! errJson 404 (sprintf "unknown workspace: %s" slug) ctx
    | Some r when r.archivedAt.IsNone ->
      return! errJson 409 "not archived" ctx
    | Some r ->
      try
        cfg.registry.SetArchived slug None
        let! n = cfg.fly.ResumeApp r.flyAppName
        let body =
          sprintf """{"slug":%s,"flyAppName":%s,"action":"unarchive","machines":%d}"""
            (JsonSerializer.Serialize (slug : string))
            (JsonSerializer.Serialize (r.flyAppName : string))
            n
        return! jsonResp 200 body ctx
      with ex ->
        eprintfn "  [provisioner/admin] unarchive %s failed: %s" slug ex.Message
        return! errJson 502 ex.Message ctx
  }

/// POST /admin/workspaces/<slug>/purge — irreversible. REQUIRES the
/// workspace to be already archived, AND the request body must echo the
/// slug back as `{"confirm":"<slug>"}`. On success: Fly app destroyed,
/// `pb_<slug>` schema dropped (if `postgresConn` is configured), and the
/// registry row removed. Per-step failures are logged but don't abort
/// later steps — the goal is "the resource is gone" even if one cleanup
/// step needs operator follow-up.
let private purgeWorkspace (cfg : ProvisionerConfig) (slug : string) : WebPart =
  fun ctx -> async {
    match cfg.registry.TryGetBySlug slug with
    | None -> return! errJson 404 (sprintf "unknown workspace: %s" slug) ctx
    | Some r when r.archivedAt.IsNone ->
      return! errJson 409 "must archive before purge" ctx
    | Some r ->
      // Require slug echo so a misclick can't nuke a tenant.
      let body = readBody ctx.request
      let confirmed =
        try
          use doc = JsonDocument.Parse (if String.IsNullOrWhiteSpace body then "{}" else body)
          match doc.RootElement.TryGetProperty "confirm" with
          | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() = slug
          | _ -> false
        with _ -> false
      if not confirmed then
        return! errJson 400 (sprintf """purge requires {"confirm":"%s"} in body""" slug) ctx
      else
        // Step 1: destroy Fly app (force=true tears down machines too).
        let mutable errs : string list = []
        try
          do! cfg.fly.DestroyApp r.flyAppName
        with ex ->
          let m = sprintf "destroy %s: %s" r.flyAppName ex.Message
          eprintfn "  [provisioner/admin] purge %s: %s" slug m
          errs <- m :: errs
        // Step 2: drop the per-workspace Postgres schema if we have a conn.
        match cfg.postgresConn with
        | None -> ()
        | Some conn ->
          try
            use c = new Npgsql.NpgsqlConnection(conn)
            c.Open()
            // Safe: schema name is constrained to pb_<slug> where slug
            // already passed `slugOk` (`[a-z][a-z0-9-]{2,31}`). Hyphens
            // need quoting in identifiers, hence the double-quotes.
            let sql = sprintf "DROP SCHEMA IF EXISTS \"pb_%s\" CASCADE" slug
            use cmd = new Npgsql.NpgsqlCommand(sql, c)
            cmd.ExecuteNonQuery() |> ignore
          with ex ->
            let m = sprintf "drop schema pb_%s: %s" slug ex.Message
            eprintfn "  [provisioner/admin] purge %s: %s" slug m
            errs <- m :: errs
        // Step 3: drop the registry row.
        try
          cfg.registry.Delete slug
        with ex ->
          let m = sprintf "registry delete: %s" ex.Message
          eprintfn "  [provisioner/admin] purge %s: %s" slug m
          errs <- m :: errs
        let errsJson =
          if List.isEmpty errs then "null"
          else
            errs
            |> List.rev
            |> List.map (fun e -> JsonSerializer.Serialize (e : string))
            |> String.concat ","
            |> sprintf "[%s]"
        let body =
          sprintf """{"slug":%s,"flyAppName":%s,"action":"purge","errors":%s}"""
            (JsonSerializer.Serialize (slug : string))
            (JsonSerializer.Serialize (r.flyAppName : string))
            errsJson
        return! jsonResp 200 body ctx
  }

/// Single self-contained HTML page for the admin portal. No external
/// assets, no framework. The operator pastes a bearer token (persisted
/// in sessionStorage) and the page fetches `/admin/workspaces`.
let private portalHtml = """<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>PulseBoard admin</title>
<meta name="viewport" content="width=device-width,initial-scale=1">
<style>
  :root { color-scheme: light dark; }
  body { font: 14px/1.45 -apple-system, system-ui, sans-serif; margin: 24px; max-width: 1100px; }
  h1 { font-size: 18px; margin: 0 0 4px; }
  .sub { color: #888; margin: 0 0 18px; font-size: 12px; }
  .bar { display: flex; gap: 8px; align-items: center; margin-bottom: 14px; flex-wrap: wrap; }
  input[type=password], input[type=text] {
    font: inherit; padding: 6px 8px; border: 1px solid #888; border-radius: 4px;
    background: transparent; color: inherit; min-width: 280px;
  }
  button {
    font: inherit; padding: 6px 14px; border: 1px solid #888; border-radius: 4px;
    background: #f4f4f4; color: #111; cursor: pointer;
  }
  @media (prefers-color-scheme: dark) { button { background: #2a2a2a; color: #eee; } }
  button:hover { border-color: #444; }
  .stat { color: #888; font-size: 12px; margin-left: auto; }
  table { width: 100%; border-collapse: collapse; }
  th, td { text-align: left; padding: 8px 10px; border-bottom: 1px solid #ddd; vertical-align: top; }
  @media (prefers-color-scheme: dark) { th, td { border-bottom-color: #333; } }
  th { font-weight: 600; font-size: 12px; color: #888; text-transform: uppercase; letter-spacing: .04em; }
  code { font: 12px/1.4 ui-monospace, SFMono-Regular, Menlo, monospace; }
  .pill {
    display: inline-block; padding: 1px 8px; border-radius: 10px; font-size: 11px;
    font-weight: 600; letter-spacing: .02em;
  }
  .pill.started { background: #d6f5dd; color: #0a5d20; }
  .pill.stopped { background: #f5d6d6; color: #6b0a0a; }
  .pill.other   { background: #ececec; color: #555; }
  @media (prefers-color-scheme: dark) {
    .pill.started { background: #133d1d; color: #8be09c; }
    .pill.stopped { background: #4a1414; color: #f0a3a3; }
    .pill.other   { background: #2a2a2a; color: #bbb; }
  }
  .err { color: #b02020; font-size: 12px; }
  .muted { color: #888; }
  .machines { display: flex; flex-direction: column; gap: 3px; }
  .machine { display: flex; gap: 6px; align-items: center; }
  .machine code { color: #888; }
  .seen { font-size: 11px; }
  .seen.fresh  { color: #0a5d20; }
  .seen.stale  { color: #b07000; }
  .seen.dead   { color: #b02020; }
  .seen.never  { color: #888; }
  @media (prefers-color-scheme: dark) {
    .seen.fresh { color: #8be09c; }
    .seen.stale { color: #e6c160; }
    .seen.dead  { color: #f0a3a3; }
  }
  .archived { display: inline-block; padding: 1px 6px; border-radius: 3px;
              font-size: 10px; font-weight: 700; letter-spacing: .05em;
              background: #f5d6d6; color: #6b0a0a; margin-left: 6px; }
  @media (prefers-color-scheme: dark) { .archived { background: #4a1414; color: #f0a3a3; } }
  tr.is-archived td { opacity: .6; }
  tr.is-archived td:first-child, tr.is-archived td:last-child { opacity: 1; }
  button.danger { border-color: #b02020; color: #b02020; }
  @media (prefers-color-scheme: dark) { button.danger { color: #f0a3a3; border-color: #b04040; } }
</style>
</head>
<body>
<h1>PulseBoard admin</h1>
<p class="sub">Provisioner: <code id="origin"></code> &nbsp;&middot;&nbsp; <span id="who" class="muted"></span></p>
<div class="bar" id="tokbar" hidden>
  <input id="tok" type="password" placeholder="bearer token" autocomplete="off" spellcheck="false">
  <button id="load">Load</button>
  <button id="reload" title="Refresh with live Fly machine state">Refresh</button>
  <label><input id="fast" type="checkbox"> skip Fly (faster)</label>
  <span class="stat" id="stat"></span>
</div>
<div class="bar" id="cookiebar" hidden>
  <button id="reloadc">Refresh</button>
  <label><input id="fastc" type="checkbox"> skip Fly (faster)</label>
  <span class="stat" id="statc"></span>
</div>
<div id="err" class="err"></div>
<table id="t" hidden>
  <thead>
    <tr>
      <th>Slug</th><th>Owner</th><th>Machines</th><th>Last seen</th><th>Created</th><th>Fly app</th><th>Actions</th>
    </tr>
  </thead>
  <tbody id="rows"></tbody>
</table>
<script>
(function () {
  var $ = function (id) { return document.getElementById(id); };
  $("origin").textContent = location.origin;
  var tokKey = "pb-admin-token";
  $("tok").value = sessionStorage.getItem(tokKey) || "";

  // Auth mode is decided once at startup: if /admin/whoami says 200,
  // we're in cookie mode (OIDC signed in) and never touch the bearer
  // input; otherwise we fall back to the legacy bearer paste box.
  var cookieMode = false;
  function isFast () {
    return cookieMode ? $("fastc").checked : $("fast").checked;
  }
  function setStat (s) {
    if (cookieMode) $("statc").textContent = s;
    else $("stat").textContent = s;
  }

  function pillClass (state) {
    if (state === "started") return "pill started";
    if (state === "stopped" || state === "destroyed" || state === "failed") return "pill stopped";
    return "pill other";
  }
  function esc (s) {
    return (s == null ? "" : String(s)).replace(/[&<>"']/g, function (c) {
      return { "&":"&amp;", "<":"&lt;", ">":"&gt;", "\"":"&quot;", "'":"&#39;" }[c];
    });
  }
  function fmtDate (s) {
    if (!s) return "";
    var d = new Date(s);
    if (isNaN(d)) return esc(s);
    return d.toISOString().replace("T", " ").replace(/\..*/, "Z");
  }
  function fmtSeen (iso, version) {
    if (!iso) return '<span class="seen never">never</span>';
    var d = new Date(iso);
    if (isNaN(d)) return esc(iso);
    var secs = Math.max(0, Math.round((Date.now() - d.getTime()) / 1000));
    var rel;
    if      (secs < 60)   rel = secs + "s ago";
    else if (secs < 3600) rel = Math.floor(secs / 60) + "m ago";
    else if (secs < 86400) rel = Math.floor(secs / 3600) + "h ago";
    else                  rel = Math.floor(secs / 86400) + "d ago";
    var cls = secs < 45 ? "fresh" : (secs < 120 ? "stale" : "dead");
    var v = version ? ' <code class="muted">v' + esc(version) + '</code>' : "";
    return '<span class="seen ' + cls + '" title="' + esc(d.toISOString()) + '">' + rel + '</span>' + v;
  }
  function renderMachines (machines, err) {
    if (err) return '<span class="err">' + esc(err) + '</span>';
    if (!machines || !machines.length) return '<span class="muted">(none)</span>';
    return '<div class="machines">' + machines.map(function (m) {
      return '<div class="machine"><span class="' + pillClass(m.state) + '">' +
        esc(m.state) + '</span> <code>' + esc(m.region) + '</code> <code>' + esc(m.id) + '</code></div>';
    }).join("") + '</div>';
  }

  async function load () {
    var tok = $("tok").value.trim();
    if (!cookieMode) {
      if (!tok) { $("err").textContent = "Paste a bearer token first."; return; }
      sessionStorage.setItem(tokKey, tok);
    }
    $("err").textContent = "";
    setStat("loading…");
    var url = "/admin/workspaces" + (isFast() ? "?machines=0" : "");
    var t0 = Date.now();
    try {
      var headers = cookieMode ? {} : { "Authorization": "Bearer " + tok };
      var r = await fetch(url, { headers: headers, credentials: "same-origin" });
      if (r.status === 401 || r.status === 403) {
        $("err").textContent = cookieMode
          ? "Session expired or unauthorized. "
          : "Auth failed (" + r.status + "). Check the token.";
        if (cookieMode) {
          // Bounce to login — the cookie has expired or been cleared.
          location.href = "/admin/login?returnTo=/admin";
          return;
        }
        setStat("");
        return;
      }
      if (!r.ok) {
        $("err").textContent = "HTTP " + r.status + ": " + (await r.text());
        setStat("");
        return;
      }
      var j = await r.json();
      $("rows").innerHTML = (j.items || []).map(function (w) {
        var slug = esc(w.slug);
        var arch = !!w.archivedAt;
        var archBadge = arch ? '<span class="archived">ARCHIVED</span>' : '';
        var actions = arch
          ? '<button data-act="unarchive" data-slug="' + slug + '">Unarchive</button> ' +
            '<button class="danger" data-act="purge" data-slug="' + slug + '">Purge</button>'
          : '<button data-act="suspend" data-slug="' + slug + '">Suspend</button> ' +
            '<button data-act="resume"  data-slug="' + slug + '">Resume</button> ' +
            '<button data-act="archive" data-slug="' + slug + '">Archive</button>';
        return '<tr class="' + (arch ? 'is-archived' : '') + '">' +
          "<td><code>" + slug + "</code>" + archBadge + "</td>" +
          "<td>" + esc(w.ownerEmail || "") + "</td>" +
          "<td>" + renderMachines(w.machines, w.machinesError) + "</td>" +
          "<td>" + fmtSeen(w.lastSeenAt, w.version) + "</td>" +
          "<td><code>" + fmtDate(w.createdAt) + "</code></td>" +
          "<td><code>" + esc(w.flyAppName) + "</code></td>" +
          "<td>" + actions + "</td>" +
          "</tr>";
      }).join("");
      $("t").hidden = false;
      setStat(j.count + " workspace(s) · " + (Date.now() - t0) + " ms");
    } catch (e) {
      $("err").textContent = String(e);
      setStat("");
    }
  }
  $("load").addEventListener("click", load);
  $("reload").addEventListener("click", load);
  $("reloadc").addEventListener("click", load);
  $("tok").addEventListener("keydown", function (e) { if (e.key === "Enter") load(); });

  // Delegate Suspend/Resume/Archive/Unarchive/Purge button clicks. We
  // deliberately confirm() for all of them — the operator is one
  // keystroke from stopping a tenant; purge requires re-typing the slug.
  document.addEventListener("click", async function (e) {
    var btn = e.target.closest("button[data-act]");
    if (!btn) return;
    var act = btn.getAttribute("data-act");
    var slug = btn.getAttribute("data-slug");
    var tok = $("tok").value.trim();
    if (!cookieMode && !tok) { $("err").textContent = "Paste a bearer token first."; return; }
    var bodyJson = null;
    if (act === "purge") {
      var typed = prompt('PURGE is irreversible. Re-type the slug "' + slug + '" to confirm:');
      if (typed !== slug) { $("err").textContent = "Purge cancelled (slug mismatch)."; return; }
      bodyJson = JSON.stringify({ confirm: slug });
    } else {
      if (!confirm(act + " workspace \"" + slug + "\"?")) return;
    }
    btn.disabled = true;
    $("err").textContent = "";
    setStat(act + " " + slug + "…");
    try {
      var headers = cookieMode ? {} : { "Authorization": "Bearer " + tok };
      if (bodyJson) headers["Content-Type"] = "application/json";
      var r = await fetch("/admin/workspaces/" + encodeURIComponent(slug) + "/" + act,
        { method: "POST", headers: headers, body: bodyJson, credentials: "same-origin" });
      var t = await r.text();
      if (!r.ok) { $("err").textContent = "HTTP " + r.status + ": " + t; return; }
      setStat(act + " " + slug + " ok — " + t);
      setTimeout(load, 500);
    } catch (ex) {
      $("err").textContent = String(ex);
    } finally {
      btn.disabled = false;
    }
  });

  // Decide auth mode on startup.
  (async function init () {
    try {
      var r = await fetch("/admin/whoami", { credentials: "same-origin" });
      if (r.ok) {
        var j = await r.json();
        cookieMode = true;
        $("cookiebar").hidden = false;
        $("who").innerHTML = "Signed in as <code>" + esc(j.email) +
          "</code> · <a href=\"/admin/logout\">Sign out</a>";
        load();
        return;
      }
    } catch (_) { /* fall through to bearer mode */ }
    // /admin/whoami missing or 401 — use the bearer paste box.
    $("tokbar").hidden = false;
    $("who").textContent = "Token is stored in sessionStorage only (cleared on tab close).";
    if ($("tok").value) load();
  })();
})();
</script>
</body>
</html>"""

let private adminPortalPage : WebPart =
  OK portalHtml
  >=> Writers.setMimeType "text/html; charset=utf-8"
  >=> Writers.setHeader "Cache-Control" "no-store"

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
    POST >=> path "/provision/heartbeat" >=> heartbeat cfg
    // OIDC login/callback/logout/whoami — mounted before the bearer-gated
    // /admin/* routes so the auth flow short-circuits. When OIDC isn't
    // configured this is a no-op that lets `choose` move to the next part.
    (match cfg.adminOidc with
     | Some oc -> AdminOidc.routes oc
     | None    -> (fun _ -> async.Return None))
    // Admin portal (token-gated; surface disappears when no tokens configured).
    GET  >=> path "/admin"            >=> adminVisible cfg adminPortalPage
    GET  >=> path "/admin/"           >=> adminVisible cfg adminPortalPage
    GET  >=> path "/admin/workspaces" >=> adminAuth cfg (listWorkspaces cfg)
    POST >=> pathScan "/admin/workspaces/%s/suspend"
                                      (fun slug -> adminAuth cfg (suspendWorkspace cfg slug))
    POST >=> pathScan "/admin/workspaces/%s/resume"
                                      (fun slug -> adminAuth cfg (resumeWorkspace cfg slug))
    POST >=> pathScan "/admin/workspaces/%s/archive"
                                      (fun slug -> adminAuth cfg (archiveWorkspace cfg slug))
    POST >=> pathScan "/admin/workspaces/%s/unarchive"
                                      (fun slug -> adminAuth cfg (unarchiveWorkspace cfg slug))
    POST >=> pathScan "/admin/workspaces/%s/purge"
                                      (fun slug -> adminAuth cfg (purgeWorkspace cfg slug))
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

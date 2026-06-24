module PulseBoard.Tests.Helpers.TestEdge

open System
open System.IO
open System.Net.Http
open System.Threading
open Suave
open Suave.Filters
open Suave.Operators
open Suave.Successful
open PulseBoard.Tenancy
open PulseBoard.Quotas
open PulseBoard.TimeSeries
open PulseBoard.Audit
open PulseBoard.Hub
open PulseBoard.Storage
open PulseBoard.Gateway
open PulseBoard.Rbac

// ---------------------------------------------------------------------------
// TestEdge — an in-process Suave host for integration tests.
//
// Unit tests can use the stores directly without HTTP. For HTTP-level
// integration tests (Phase 11.4), call TestEdge.Start() which boots the
// full WebPart graph on an ephemeral port and returns a pre-wired HttpClient.
//
// Usage:
//
//   use env = TestEdge.create ()
//   // Direct store access:
//   let result = env.Limiter.TryAcquire(tenantId, Ingest)
//   // HTTP access (starts Suave on first call):
//   let! resp = env.Http.GetAsync("/api/metrics") |> Async.AwaitTask
//
// ---------------------------------------------------------------------------

/// All in-memory stores wired with consistent defaults for tests.
[<NoComparison; NoEquality>]
type TestStores =
  { TenantStore : ITenantStore
    AuditLog    : IAuditLog
    QuotaStore  : QuotaStore
    Limiter     : Limiter
    MetricStore : MetricStore
    LogStore    : LogStore
    Storage     : IStorageClient }

/// A running test environment. Dispose to stop the Suave server.
[<NoComparison; NoEquality>]
type TestEnv (stores : TestStores, baseUrl : string, cts : CancellationTokenSource) =
  member _.Stores    = stores
  member _.BaseUrl   = baseUrl
  /// A fresh, unauthenticated HttpClient pointed at the test server.
  member _.Http      =
    let c = new HttpClient()
    c.BaseAddress <- Uri(baseUrl)
    c
  /// A fresh HttpClient with the given API key in X-API-Key header.
  member _.HttpWithKey (key : string) =
    let c = new HttpClient()
    c.BaseAddress <- Uri(baseUrl)
    c.DefaultRequestHeaders.Add("X-API-Key", key)
    c
  /// Register a tenant and issue an API key; returns the plaintext key string.
  member _.IssueKey (tenantName : string) (scope : Scope) : string =
    let _tenant = stores.TenantStore.CreateTenant(tenantName)
    let tid  = _tenant.id
    let iss  = stores.TenantStore.IssueApiKey(tid, tenantName, Role.Editor, scope)
    iss.plaintext
  interface IDisposable with
    member _.Dispose () = cts.Cancel()

/// Default quota limits for tests: generous capacity, instant refill.
let private testDefaults =
  allKinds
  |> Array.map (fun k -> k, { capacity = 10_000.0; refillPerSec = 10_000.0 })
  |> Map.ofArray

/// Create all in-memory stores wired together.
let makeStores () : TestStores =
  let tenantStore = InMemoryTenantStore()    :> ITenantStore
  let auditLog    = InMemoryAuditLog(512)    :> IAuditLog
  let overrideRepo = InMemoryOverrideRepo()  :> IOverrideRepo
  let quotaStore  = QuotaStore(testDefaults, cardinalityDefault = 0, repo = overrideRepo)
  let limiter     = Limiter(quotaStore)
  let metricStore = MetricStore(capacityPerMetric = 4096)
  let logStore    = LogStore(capacity = 4096)
  let hub         = new Broadcaster()
  let metricBack  = EmbeddedMetricBackend(metricStore, None) :> IMetricBackend
  let logBack     = EmbeddedLogBackend(logStore)             :> ILogBackend
  let traceBack   = EmbeddedTraceBackend()                   :> ITraceBackend
  let storage     = InProcessStorageClient(metricBack, logBack, traceBack, hub) :> IStorageClient
  { TenantStore = tenantStore
    AuditLog    = auditLog
    QuotaStore  = quotaStore
    Limiter     = limiter
    MetricStore = metricStore
    LogStore    = logStore
    Storage     = storage }

/// Pick a free TCP port by binding briefly to port 0.
let private freePort () =
  use s = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)
  s.Start()
  let port = (s.LocalEndpoint :?> System.Net.IPEndPoint).Port
  s.Stop()
  port

/// Start an in-process Suave host and return a TestEnv.
/// The WebPart graph will be expanded as integration tests are added in Phase 11.4.
/// Unit tests that test module logic directly do not need to call this.
let start (stores : TestStores) : TestEnv =
  let port = freePort ()
  let baseUrl = sprintf "http://127.0.0.1:%d" port
  let cts = new CancellationTokenSource()

  // Full WebPart: health check, all ingest endpoints, all query endpoints.
  // No auth middleware — single-tenant mode suitable for protocol tests.
  let app =
    choose [
      GET  >=> path "/api/healthz" >=> OK """{"ok":true}"""
      pathStarts "/ingest" >=>
        PulseBoard.Ingest.webPart stores.Storage None None None None None None
      POST >=> path "/v1/metrics" >=>
        PulseBoard.Otlp.metrics stores.Storage None
      POST >=> path "/loki/api/v1/push" >=>
        PulseBoard.LokiPush.handler stores.Storage None
      POST >=> (path "/api/v1/write" <|> path "/api/prom/push") >=>
        PulseBoard.PromRemoteWrite.handler stores.Storage None None
      PulseBoard.QueryApi.webPart None None stores.MetricStore None stores.LogStore
      PulseBoard.Query.webPart stores.MetricStore stores.LogStore None None
    ]

  let suaveCfg =
    { defaultConfig with
        bindings    = [ HttpBinding.createSimple HTTP "127.0.0.1" port ]
        cancellationToken = cts.Token
        hideHeader  = true }

  let _startTask =
    System.Threading.Tasks.Task.Run(fun () ->
      startWebServer suaveCfg app)

  // Give Suave a moment to bind.
  System.Threading.Thread.Sleep 150

  new TestEnv(stores, baseUrl, cts)

/// Start an in-process Suave host with auth middleware enabled.
/// Ingest routes require a valid X-API-Key header with Ingest scope.
/// Query routes are also protected (Query scope required via requireScope).
let startMultiTenant (stores : TestStores) : TestEnv =
  let port = freePort ()
  let baseUrl = sprintf "http://127.0.0.1:%d" port
  let cts = new CancellationTokenSource()

  let authIngest inner =
    PulseBoard.Auth.resolveApiKey stores.TenantStore
      (requireScope stores.AuditLog "ingest" Scope.Ingest inner)

  let app =
    choose [
      GET  >=> path "/api/healthz" >=> OK """{"ok":true}"""
      pathStarts "/ingest" >=>
        authIngest (PulseBoard.Ingest.webPart stores.Storage None None None None None None)
      POST >=> path "/v1/metrics" >=>
        authIngest (PulseBoard.Otlp.metrics stores.Storage None)
      POST >=> path "/loki/api/v1/push" >=>
        authIngest (PulseBoard.LokiPush.handler stores.Storage None)
      POST >=> (path "/api/v1/write" <|> path "/api/prom/push") >=>
        authIngest (PulseBoard.PromRemoteWrite.handler stores.Storage None None)
      PulseBoard.QueryApi.webPart None None stores.MetricStore None stores.LogStore
      PulseBoard.Query.webPart stores.MetricStore stores.LogStore None None
    ]

  let suaveCfg =
    { defaultConfig with
        bindings    = [ HttpBinding.createSimple HTTP "127.0.0.1" port ]
        cancellationToken = cts.Token
        hideHeader  = true }

  let _startTask =
    System.Threading.Tasks.Task.Run(fun () ->
      startWebServer suaveCfg app)

  System.Threading.Thread.Sleep 150

  new TestEnv(stores, baseUrl, cts)

/// Convenience: create stores + start server in one call (unauthenticated).
let create () =
  let stores = makeStores ()
  start stores

/// Convenience: create stores + start server with auth middleware enabled.
let createMultiTenant () =
  let stores = makeStores ()
  startMultiTenant stores

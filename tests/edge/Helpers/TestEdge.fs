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
  member _.Http      =
    let c = new HttpClient()
    c.BaseAddress <- Uri(baseUrl)
    c
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
  let hub         = Broadcaster()
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

  // Minimal WebPart: health check, ingest, and query routes.
  // Ingest and Query WebParts are wired with no quotas / secrets / meters
  // so tests exercise the storage path without multi-tenant overhead.
  let app =
    choose [
      GET  >=> path "/api/healthz" >=> OK """{"ok":true}"""
      pathStarts "/ingest" >=>
        PulseBoard.Ingest.webPart stores.Storage None None None None None
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

  TestEnv(stores, baseUrl, cts)

/// Convenience: create stores + start server in one call.
let create () =
  let stores = makeStores ()
  start stores

module PulseBoard.Self

open System
open System.Threading
open System.Threading.Tasks
open PulseBoard.Tenancy
open PulseBoard.TimeSeries
open PulseBoard.Dashboards

// -- PLAN.md Phase 6 #6 -----------------------------------------------------
// Self-observability. PulseBoard records its own `pulse_*` metrics into the
// process-wide MetricStore today; this module reserves a dedicated meta
// tenant, seeds a curated dashboard for those series, and runs a small
// background loop that derives SLO summary metrics from the raw counters.
//
//   metaTenantId = TenantId "__meta__"
//   slug         = "__meta__"
//
// The store is multi-tenant by RBAC but the in-process MetricStore is not
// tenant-partitioned, so the meta dashboard reads the same series the rest
// of the UI sees — the tenant boundary's value here is the RBAC gate that
// keeps non-admin operators from accidentally watching platform internals.
// Production setups that physically isolate `__meta__` should route platform
// metrics to a dedicated Mimir org and configure the meta tenant against
// that backend.

let [<Literal>] metaSlug = "__meta__"
let metaTenantId         = TenantId metaSlug

// ---------------------------------------------------------------------------
// Meta dashboard
// ---------------------------------------------------------------------------

let private metaDashboard () : Dashboard =
  let now = DateTimeOffset.UtcNow
  let mkPanel id title pt lang expr x y w h opts =
    { id        = id
      title     = title
      panelType = pt
      queryLang = lang
      expr      = expr
      x = x; y = y; w = w; h = h
      options   = Map.ofList opts }
  { id           = "pulse-self"
    title        = "PulseBoard \u2014 Self-Observability"
    timeRangeSec = 3600
    refreshSec   = 15
    vars         = "[]"
    panels =
      [|
        mkPanel "p-ingest"      "Ingest throughput"        "timeseries" "native"
                "pulse_ingest_total"          0 0 6 3 [ "unit", "ops" ]
        mkPanel "p-ingest-err"  "Ingest errors"            "timeseries" "native"
                "pulse_ingest_errors_total"   6 0 6 3 [ "unit", "ops" ]
        mkPanel "p-query"       "Query throughput"         "timeseries" "native"
                "pulse_query_total"           0 3 6 3 [ "unit", "ops" ]
        mkPanel "p-query-p99"   "Query latency (p99)"      "timeseries" "native"
                "pulse_query_p99_ms"          6 3 6 3 [ "unit", "ms" ]
        mkPanel "p-notify-att"  "Notify attempts"          "timeseries" "native"
                "pulse_notify_attempts_total" 0 6 4 3 [ "unit", "ops" ]
        mkPanel "p-notify-fail" "Notify failures"          "timeseries" "native"
                "pulse_notify_failures_total" 4 6 4 3 [ "unit", "ops" ]
        mkPanel "p-rule"        "Rule eval (seconds)"      "timeseries" "native"
                "pulse_rule_eval_seconds"     8 6 4 3 [ "unit", "s" ]
        mkPanel "p-slo-ingest"  "SLO \u2014 ingest success ratio (5m)"
                "stat"       "native"
                "pulse_slo_ingest_success_ratio_5m"
                0 9 4 2 [ "unit", "ratio" ]
        mkPanel "p-slo-notify"  "SLO \u2014 notify success ratio (5m)"
                "stat"       "native"
                "pulse_slo_notify_success_ratio_5m"
                4 9 4 2 [ "unit", "ratio" ]
        mkPanel "p-quota-deny"  "Quota denies"             "stat"       "native"
                "pulse_quota_deny_total"      8 9 4 2 []
      |]
    createdAt = now
    updatedAt = now }

/// Idempotently ensure the meta tenant + its curated dashboard exist.
/// Safe to call on every start.
let bootstrap (tenants : ITenantStore) (repo : IDashboardRepo) : Tenant =
  // CreateTenant in the in-memory store is idempotent by slug; the
  // Postgres-backed store uses the same contract.
  let t = tenants.CreateTenant metaSlug
  // IMPORTANT: dashboards must be keyed by the tenant's *real* id (the
  // value the auth layer attaches to inbound requests), not the slug.
  // Persisted stores (Postgres) generate an opaque id per tenant, so
  // writing under `TenantId "__meta__"` would place the dashboard in a
  // directory that no request ever resolves to and the API would then
  // auto-seed a default `overview` on first hit instead.
  match repo.List t.id with
  | xs when xs.Length = 0 -> repo.Upsert(t.id, metaDashboard ())
  | _ -> ()
  t

// ---------------------------------------------------------------------------
// SLO derivation loop
// ---------------------------------------------------------------------------
// Reads the raw `pulse_*` counter series from the global MetricStore over
// the last 5 minutes, derives success ratios, and writes them back as new
// series the meta dashboard can plot directly. Counter values in this
// codebase are absolute (Record is called with `value = 1.0` per event),
// so a windowed sum is the event count.

let private windowSum (ms : MetricStore) (name : string) (sinceMs : int64) : float =
  let pts = ms.GetSince(name, sinceMs)
  let mutable s = 0.0
  for p in pts do s <- s + p.value
  s

let private safeRatio (success : float) (failure : float) =
  let total = success + failure
  if total > 0.0 then success / total else 1.0

/// Start a CancellationToken-bound background task that emits derived SLO
/// metrics every `intervalSec` seconds. Idempotent records (any failures
/// are swallowed). Returns the started Task so callers can observe it.
let startSloLoop (ms : MetricStore) (intervalSec : int)
                 (ct : CancellationToken) : Task =
  let interval = max 5 intervalSec
  let windowMs = 5L * 60L * 1000L
  Task.Run(System.Func<Task>(fun () ->
    task {
      try
        while not ct.IsCancellationRequested do
          try
            let now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            let since = now - windowMs
            let ingestOk  = windowSum ms "pulse_ingest_total"          since
            let ingestErr = windowSum ms "pulse_ingest_errors_total"   since
            let ingestRatio =
              safeRatio (max 0.0 (ingestOk - ingestErr)) ingestErr
            ms.Record("pulse_slo_ingest_success_ratio_5m",
                      { ts = now; value = ingestRatio })

            let notifyAtt  = windowSum ms "pulse_notify_attempts_total" since
            let notifyFail = windowSum ms "pulse_notify_failures_total" since
            let notifyRatio =
              safeRatio (max 0.0 (notifyAtt - notifyFail)) notifyFail
            ms.Record("pulse_slo_notify_success_ratio_5m",
                      { ts = now; value = notifyRatio })
          with _ -> ()
          do! Task.Delay(TimeSpan.FromSeconds(float interval), ct)
      with
      | :? OperationCanceledException -> ()
      | _ -> ()
    } :> Task))

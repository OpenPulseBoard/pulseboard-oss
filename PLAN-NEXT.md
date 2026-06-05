# PulseBoard — Next Steps Toward a Commercial Product

> Status: **planning** · Continues [PLAN.md](PLAN.md) (Phases 1–10 complete)
> Theme: **ship quality, ship breadth, ship the moat**

The first ten phases built the substrate: ingest, storage abstraction, query, rules, routing, on-call, billing, plans, member portal, multi-region deploy. We have a *system*. We do not yet have a *product* customers will pay $99/mo to use over Grafana Cloud.

The next four phases close that gap. They are designed to run **in parallel** by independent workstreams.

| Workstream | Phase | What | Owns the moat? |
| --- | --- | --- | --- |
| A — Quality | **11** | Test pyramid, CI gates, perf/chaos harness | No — table stakes |
| B — Visualization | **12** | 20+ panel types, panel SDK, dashboard polish | Partially |
| C — Collection | **13** | PulseAgent (Alloy-equivalent) + integration recipes | **Yes** — ease-of-use |
| D — Differentiation | **14** | Inline runbooks, NL queries, GitOps, status pages, correlation | **Yes** — features incumbents don't have |

---

## Phase 11 — Test Pyramid & CI Quality Gates

**Problem.** `src/edge/` has zero automated tests. Every refactor today is brave. Every release is hope. Before we sell uptime to anyone, we have to prove our own.

### 11.1 Test infrastructure

1. **Add `tests/edge/PulseBoard.Tests.fsproj`** (xUnit + FsUnit + FsCheck for property tests). Hook into `dotnet test` from CI.
2. **Add `tests/cloud/PulseBoard.Cloud.Tests.fsproj`** mirroring for the cloud repo.
3. **Test data harness.** A reusable `TestEdge` fixture that boots a Suave host on an ephemeral port with in-memory backends, plus a `TestPostgres` fixture using Testcontainers for Postgres-backed paths.
4. **Coverage gate.** Coverlet + ReportGenerator. CI fails PRs that drop line coverage on touched files below 70%. Target 60% overall by end of Phase 11.

### 11.2 Unit tests (target: every module with logic)

Priority order — biggest blast radius first:

| Module | What to cover |
| --- | --- |
| `Rules.fs` | PromQL/LogQL eval correctness, `for` window pending→firing, group sharding |
| `Routing.fs` | Route tree match, silence/mute/inhibit precedence, group dedup, `repeatInterval` |
| `NotifyQueue.fs` | Journal replay, lease ownership, retry backoff, DLQ promotion |
| `OnCall.fs` | Rotation math at boundary timestamps, override precedence, escalation step delay |
| `Quotas.fs` | Token bucket fairness, burst handling, plan-tier defaults |
| `Retention.fs` | Per-tenant override resolution, compactor doesn't evict newer-than-max |
| `Rollups.fs` | Bucket aggregation (avg/min/max/sum/count) idempotency, partial bucket correctness |
| `Secrets.fs` | KEK/DEK envelope encrypt/decrypt roundtrip, `[[pii:…]]` marker rewrite |
| `Tenancy.fs` | Argon2id verify roundtrip + PBKDF2 legacy fallback, scope enforcement |
| `Plans.fs`, `Pricing.fs`, `Billing.fs` | Soft/hard cap math, overage line items, drain atomicity |
| `Spans.fs` | Service-map edge attribution, percentile correctness on synthetic spans |
| `AiAssist.fs` | Echo provider spike detection on golden fixtures |

### 11.3 Property tests (FsCheck)

- **Rollup invariant:** for any sequence of points, `sum(buckets) == sum(raw)` and `count(buckets) == count(raw)`.
- **Quota fairness:** N concurrent token-bucket callers, sum of admits ≤ capacity + drift.
- **Routing terminality:** every fired alert lands in exactly one final group per receiver (modulo `continue`).
- **NotifyQueue at-least-once:** any sequence of crashes + restarts delivers every enqueued message ≥ 1 time within `maxAttempts`.

### 11.4 Integration tests

End-to-end black-box scenarios driven through the public HTTP surface:

1. Two tenants signed up; cross-tenant token swap returns 401 (regression for Phase 1 acceptance).
2. OTLP/HTTP metric → `/api/prom/api/v1/query` returns it.
3. Loki push → `/api/loki/api/v1/query_range` returns it.
4. Prom `remote_write` (real snappy-protobuf payload) → stored.
5. Rule with `for=5s` flaps three times → routing groups into one notification, DLQ stays empty.
6. Stripe webhook `invoice.payment_failed` (cloud) → workspace moves to `overdue`, then archived after grace.
7. Full signup → first ingest → dashboard renders → alert fires → ack flows.

### 11.5 Performance & chaos harness

- **Bench suite** under `tests/bench/` (BenchmarkDotNet): ingest throughput per receiver, query p99 on rolled-up vs. raw, alert eval per group, NotifyQueue enqueue/dispatch.
- **k6 load profile** in CI nightly: 10k series × 1k samples/s for 10 minutes, asserts `pulse_query_p99_ms < 1000` and zero dropped notifications.
- **Chaos toolkit** (`tests/chaos/`): kill-edge-pod, kill-postgres, kill-Mimir-ingester smoke; assert recovery SLOs.

### 11.6 Tooling

- Pre-commit hook: `dotnet format` + `dotnet test --filter Category=Fast`.
- PR template enforces "tests added or N/A justified".
- `make test`, `make bench`, `make chaos` targets.

**Acceptance:** PR-gated CI runs the full unit + integration suite under 5 minutes; nightly runs bench + chaos and posts results to the self-observability dashboard.

---

## Phase 12 — Visualization & Dashboard Breadth

**Problem.** Today the SPA supports four panel types (`timeseries`, `stat`, `logs`, `table`). Grafana has 25+. Customers refuse to migrate if their existing dashboards can't be re-created.

### 12.1 Panel SDK (must come first)

Refactor `src/edge/wwwroot/index.html` so panels are pluggable, not hard-coded:

```js
PulseBoard.registerPanel({
  type: "heatmap",
  schema: { /* JSON schema for panel.options */ },
  queryShape: "matrix",      // scalar | vector | matrix | logs | spans | nodes | edges
  render: (el, frame, opts) => { /* draw into el */ },
  editor: (opts, onChange) => { /* options form */ }
})
```

Frame model: a tiny in-house DataFrame (`{fields:[{name,type,values}]}`) lifted from Grafana's, encoded straight off `/api/prom/api/v1/query_range`, `/api/loki/api/v1/query_range`, `/api/traces`, `/api/servicemap`. No third-party data layer.

Vendor minimal libs (all MIT, all <100 KB gzipped):
- `uPlot` (already vendored) — time series, bar, stat, trend.
- `echarts-lite` — heatmap, candlestick, pie, gauge, geomap.
- `d3-hierarchy` + custom SVG — node graph, flame graph, service map (already partially built).
- `leaflet` — geomap base layer.
- `marked` — markdown panel.

### 12.2 Panel types (delivered in three waves)

**Wave A — high demand, low effort (ship first):**

- Stat (✅ have) + sparkline option
- Bar gauge (horizontal + vertical)
- Gauge (round, threshold bands)
- Pie / donut
- Bar chart (categorical)
- Histogram
- State timeline (state changes over time)
- Status history (periodic state heat strip)
- Text (markdown + sanitized HTML)
- Dashboard list widget
- Alert list widget
- Annotations list widget

**Wave B — moderate effort, high commercial value:**

- Heatmap (Prometheus histogram-native)
- Trend (non-time numeric x)
- XY chart (scatter / bubble)
- Candlestick (OHLC) — opens us to finance + dev-trading customers
- Traces panel (extend the existing modal into an embeddable waterfall)
- Flame graph (we'll need a profiling ingest path — see Phase 14.7)
- News (RSS via server proxy to avoid CORS)

**Wave C — hard but moat-worthy:**

- Node graph (extend the existing service-map SVG to a general DAG renderer with manual + auto layout)
- Canvas (free-form drag-and-drop layout with bind-to-query elements — a unique selling point vs. Grafana's still-beta Canvas)
- Geomap (Leaflet + GeoJSON overlays; latency-per-region heat; click-to-drill)

### 12.3 Dashboard ergonomics (the "easy" in ease-of-use)

These are where we beat Grafana on usability:

1. **Template variables** with type-aware pickers (multi-select, query-driven, regex filter).
2. **Drilldown links** — `Cmd+click any data point → opens a templated dashboard / log query / trace`.
3. **Compare-time** — overlay "now vs. 7 days ago" as a single toggle.
4. **Saved views** per dashboard (per-user range + variable presets).
5. **Versioning** — every dashboard PUT is a new revision; diff + rollback in the UI; optional Git sync (see 14.5).
6. **Sharing** — signed public snapshot links with expiry; embed `<iframe>` for marketing pages.
7. **Live mode** — already have WS hub; expose a toggle "stream this dashboard" that bypasses polling. **This is a real moat** — DD/NR refresh dashboards every 10s; ours can be sub-second.

### 12.4 Dashboard library (Phase 13 dependency)

Curate a built-in catalog of dashboards keyed off the integration recipes Phase 13 ships: Node.js, Python, Go, Java JVM, Postgres, Redis, NGINX, Kubernetes, Linux host, Docker daemon. Each catalog entry is a JSON `Dashboard` plus a list of required metrics and a "import" button that diffs against what the tenant already has.

**Acceptance:** A new tenant clicks "Add Postgres" in the integration wizard, follows three steps, and within 60 seconds has a populated Postgres dashboard with metrics, logs, three pre-configured alerts, and a runbook link — without ever editing a dashboard JSON or alert rule by hand.

---

## Phase 13 — PulseAgent: the Telemetry Collector

**Problem.** Today customers point existing OTel/Prom/Loki agents at us. That's fine for migrations but terrible for the cold-start case ("I have a Linux box, give me a dashboard in 5 minutes"). Grafana Alloy and the OTel Collector solve this — but their UX is a 600-line YAML file.

We build a competing collector that wins on **debuggability and zero-config**.

### 13.1 Architecture

Single-binary Go (or Rust) agent: `pulseagent`. Why not F#? Because the agent must run on edge devices, embedded systems, and 100MB containers — `.NET 10` + AOT works but Go's static binary is simpler. (Open question: revisit if Native AOT for .NET hits a small enough footprint by ship time.)

**Component model** (Alloy-inspired):

```hcl
# /etc/pulseagent/agent.river  (HCL-ish, but valid w/o any imports)
source.host_metrics "self" { interval = "15s" }
source.journald     "syslog" {}
source.docker_logs  "containers" {}

# everything below is auto-wired — agents discovers sinks from the
# `target` block, no copy-paste connectors needed.

target "pulseboard" {
  url    = "https://acme-7f3a.pulseboard.cloud"
  apikey = env("PULSEBOARD_KEY")
}
```

Auto-wiring contract: every `source.*` emits typed signals; the agent solves a graph from sources → optional `processor.*` → the configured `target.*`. New sources don't need new pipelines; they just appear in the data.

### 13.2 Built-in sources (Wave 1)

| Source | OS coverage | Equivalent today |
| --- | --- | --- |
| `host_metrics` | Linux / macOS / Windows | node_exporter |
| `journald` | Linux | promtail |
| `windows_event_log` | Windows | winlogbeat |
| `file_logs` (tail w/ multiline) | All | filebeat |
| `docker_logs` + `docker_stats` | All | cadvisor |
| `kubernetes_pods` (DaemonSet mode) | k8s | Alloy + OTel |
| `prometheus_scrape` | All | prom server |
| `otlp_receiver` (in-process) | All | otel-collector |

### 13.3 Built-in processors

- `processor.batch` (default)
- `processor.relabel` (Prometheus relabel grammar — keep it familiar)
- `processor.redact_pii` (regex + structured) — auto-tags fields with `[[pii:…]]` markers so our server-side envelope encryption applies (see `Secrets.fs`)
- `processor.cardinality_guard` — local kill-switch that drops series exceeding a tenant-supplied budget *before* they hit the wire (saves the customer's bill)
- `processor.transform` (tiny embedded JSON template DSL — no Lua, no JS, predictable resource usage)

### 13.4 What makes it a moat: **the Live Debugger**

Every other collector debugs via "edit YAML, restart, tail logs". Ours ships with a built-in web UI on `localhost:8000`:

1. **Signal Inspector** — live view of every signal flowing through every component, sampled at 1/s with full payload + processed payload side-by-side. Pause, step, replay.
2. **Pipeline graph** — visual DAG of `source → processor → target` with throughput numbers on each edge, exactly like Alloy but with click-to-inspect on each edge.
3. **Config linter** with live validation as you type, including "this label will explode cardinality" warnings backed by a quick local sample.
4. **"Why isn't this metric showing up?"** flow — paste a metric name, agent answers "dropped at relabel rule 3 because regex didn't match; here's the payload".
5. **Dry-run mode** — `pulseagent run --dry-run` prints what *would* be sent without actually shipping.

### 13.5 Deployment shapes

- **Single binary install:** `curl https://pulseboard.cloud/install.sh | sh` writes the binary + a systemd unit, enrolls via short-lived enrollment token from the portal.
- **Container:** `docker run -e PULSEBOARD_KEY=... ghcr.io/pulseboard/agent`.
- **Kubernetes:** Helm chart + a one-line `kubectl apply` that uses the K8s API to auto-discover pods, no annotations needed.
- **Fleet management:** agents check in to `/api/agent/v1/checkin` every 60s; the portal lists agents with status, version, last-seen, drift from desired config. One-click "rollout config v17 to all production hosts".

### 13.6 Edge endpoints (server side)

Add to `src/edge/`:

- `POST /api/agent/v1/enroll` — exchanges enrollment token for a long-lived agent cert.
- `POST /api/agent/v1/checkin` — agent heartbeat + version + config hash.
- `GET /api/agent/v1/config` — returns desired config (signed) for a given agent group.
- `GET /api/agents` (portal-side) — fleet listing.

### 13.7 Repo layout decision

Pulseagent lives in **a new public OSS repo `pulseboard-agent`** (MIT, max adoption). This decouples agent release cycle from edge, and lets non-PulseBoard users adopt the agent (it speaks OTLP and Prom remote_write to anyone). Lead-gen funnel.

**Acceptance:** A user on a fresh Ubuntu VM runs the one-line install, types nothing else, and within 90 seconds sees host CPU/mem/disk metrics and journald logs in their workspace dashboard. Config UI on `localhost:8000` shows the signal flow live.

---

## Phase 14 — Commercial Differentiation (the moat)

Pick 4–5 from this list and over-invest. The rest stay backlog.

### 14.1 Inline runbooks for alerts ★

Every alert rule gets an optional markdown `runbook` field. When the alert fires:

- Notification body includes the rendered runbook (truncated) + a deep link.
- Acker is presented the runbook in the portal; checkboxes track progress; completion time recorded as `pulse_runbook_step_seconds`.
- Post-incident view computes "MTTR-by-runbook" and "skipped steps" to feed runbook quality improvements.

Nobody ships this end-to-end today. It's a small build (2 weeks) for a wildly disproportionate sales pitch.

### 14.2 Natural-language query + dashboard build ★

Extend `AiAssist.fs`:

- `POST /api/ai/query` — `{question, scope:"metrics"|"logs"|"traces"}` → `{promql|logql, explanation}`. Server-side context: known metric names, label cardinality samples, recent series. SaaS edge uses an external LLM; OSS edge ships a deterministic rule-based fallback (keyword → metric name mapping seeded from labels).
- `POST /api/ai/dashboard` — `{description}` → a complete `Dashboard` JSON. "Build me an SLO dashboard for the checkout service" returns 6 panels wired to real queries.
- **Guardrails:** every AI-generated query is shown with the prose explanation; one-click "edit" before run. Telemetry records accept/reject ratios.

### 14.3 Cost guard rails ★

Build on existing `Costs.fs`:

- **Cardinality killer**: when a series exceeds a threshold (e.g. > 10× p99 of cohort), portal surfaces it with **one-click "drop this label everywhere"** that writes the relabel rule into the agent's desired config (Phase 13) *and* into the edge's drop list. Closes the loop from problem to mitigation in ten seconds.
- **Predicted monthly bill** on the workspace home, with the trajectory split per pillar, per team. Refreshes hourly. Drill-down: "which series is the top contributor".
- **Budget alerts** as native alert rules — "alert me when projected monthly ingest exceeds $X".

### 14.4 End-to-end correlation by default

We already have metrics + logs + traces + RUM in one process. Wire the UI so:

- Every metric panel has a "show logs for this spike" right-click → opens Explore with `service=<derived>` and the spike time window.
- Every alert notification includes top 3 correlated log lines + the slowest trace from the breach window, auto-attached at fire time (cached in the alert state).
- Every trace span has "show metrics for this service" jump.
- Exemplars surfaced on histograms by default (no opt-in config like Grafana).

### 14.5 GitOps for everything

- **Terraform provider** (`pulseboard/pulseboard`) covering tenants, API keys, dashboards, rule groups, routing, on-call schedules, integrations. _(deferred — lives in a separate Go repo)_
- **Git-sync mode** on each workspace ✅: configure a Git URL + path; the workspace pulls dashboards/rules from `dashboards/` and `rules/` directories on a 30s cadence; CRUD APIs return 405 in this mode. _(`GitSync.fs`; `--gitops-url=`/`PULSE_GITOPS_URL` + branch/path/interval/ssh-key/token-env/prune flags; HTTPS-token & SSH auth; filename = stable resource id; prune-on-reconcile; `GET /api/gitops/status`.)_
- **Export-as-code** on every UI surface ✅: every dashboard / rule / route has "copy as Terraform / copy as YAML" buttons. _(`ExportCode.fs`; `GET /api/export/{dashboards/<id>,rules/<id>,routing}?format=tf|yaml`; SPA `</> Code` modal on the dashboard toolbar and per rule group.)_

### 14.6 Built-in public status pages ✅

Reuse existing SLO computation in `Self.fs`. Per workspace:

- Define one or more "status components" backed by a query / SLO.
- Auto-publish `status.<workspace>.pulseboard.cloud` (or BYO domain) with uptime history, current incidents auto-derived from active alert groups, scheduled maintenance windows.
- Customers cancel their statuspage.io contract; we add a $29/mo line item.

**Shipped:** `StatusPages.fs` (model + validate + JSON codecs + `IStatusStore`/`FileStatusStore` + live-status renderer reusing the MetricStore series, synthetic checks, and firing alerts) and `PgStatusStore.fs` (Postgres backend). Components are backed by either a 14.8 synthetic check (`pulse_synthetic_up`) or any metric series + comparison; uptime is averaged over a 24h window; incidents auto-derive from `ruleEvaluator.Active` firing alerts; operator-authored maintenance windows. Admin CRUD under `/api/status/pages` (Query scope) with a `/preview` endpoint; unauthenticated public surface `GET /api/public/status[/<slug>]` + a self-contained `status.html` viewer at `/status[/<slug>]` (auto-refresh 30s). SPA "Status" tab for page/component/maintenance CRUD. 16 unit tests in `StatusPagesTests.fs`.

### 14.7 Continuous profiling (defer evaluation until Phase 12 ships flame graph)

Add `IProfileBackend` next to the existing storage backends. Embed **Pyroscope** (OSS, Go) as the storage tier; ingest via pprof + JFR. Pairs with the flame graph panel from Phase 12. Pulse-style "AI explains this profile" on top.

### 14.8 Synthetic & uptime checks ★

Run small `http`/`tcp`/`dns` probes from edge regions. Tenant defines targets in the portal; results land as metrics + logs + alerts using the same pipeline. Direct competitor to Pingdom / UptimeRobot, but co-located with the rest of the data. Multi-region matrix view ("up from us-east, down from eu-west").

### 14.9 Slack-native dashboards

Slack app where `/pulse <query>` returns a live-rendering panel (server-rendered SVG, refresh button updates in place). On-call channel members can ack/silence/resolve from Slack. Datadog charges $$$ for this; we make it the default.

### 14.10 Mobile on-call app

React Native, minimum viable: push notification, ack, escalate, view the runbook from 14.1, view the auto-attached log/trace from 14.4. Wins us every on-call engineer who currently fumbles PagerDuty's app at 3am.

### 14.11 Preview environments per PR

GitHub App: every PR gets a temporary workspace (`pr-<n>-<repo>.preview.pulseboard.cloud`); ingestion runs for the PR's preview deploy; metrics + alerts attached to PR comments ("p99 regressed 18% on the checkout endpoint"). Auto-torn-down on merge. Nobody offers this; it's directly tied to the Phase 9 provisioner.

### 14.12 Cross-tenant benchmarks (opt-in)

Anonymized cohort percentiles — "your Postgres query p99 is in the 73rd percentile of similar services on PulseBoard." Pure margin: data we already have, repackaged as insight. Tiny build, big retention story.

---

## Recommended sequencing

```
month 0 ─┬─ 11 Quality          (testing infra + first 50% coverage)
         ├─ 12 Visualization    (panel SDK + Wave A panels)
         └─ 13 PulseAgent       (architecture spike + host_metrics MVP)

month 3 ─┬─ 11 cont.            (integration + chaos suites)
         ├─ 12 cont.            (Wave B panels + library)
         ├─ 13 cont.            (k8s + docker sources, live debugger)
         └─ 14.1 Runbooks       (small, wins demos)

month 6 ─┬─ 12 Wave C panels    (canvas + geomap)
         ├─ 13 fleet mgmt + dashboard library wiring
         ├─ 14.2 NL queries
         ├─ 14.3 Cost guard rails
         └─ 14.5 Terraform provider

month 9 ─┬─ 14.4 Correlation default
         ├─ 14.6 Status pages
         ├─ 14.8 Synthetic checks
         └─ 14.9 Slack-native

month 12 ─ Public GA. Pick one of {14.7 profiling, 14.10 mobile, 14.11 PR previews, 14.12 benchmarks} to launch with.
```

---

## Open questions to resolve before kickoff

1. **Agent language.** Go or Rust for `pulseagent`? Go wins on speed-to-MVP and existing OTel ecosystem code we can borrow; Rust wins on resource footprint. Recommend Go unless someone on the team is already strong in Rust.
2. **AI provider.** Hosted-only (OpenAI/Anthropic) or BYO endpoint (vLLM/Ollama)? Affects enterprise data-residency story for 14.2.
3. **Panel library license.** Pulling in ECharts (Apache-2.0) is fine; confirm we're OK with the bundle hit (~250 KB gzipped lazy-loaded per panel type).
4. **Status pages** (14.6) — separate billable add-on or bundled into Pro? Margin vs. acquisition trade-off.
5. **Pulseagent repo licensing** — MIT (recommended for max adoption) or Apache-2.0?

---

## Verification per phase

| Phase | Acceptance |
| --- | --- |
| 11 | CI green: 60% coverage workspace-wide; nightly bench + chaos posts to self-observability; PR template enforced. |
| 12 | All Wave A + B panels render from a single Dashboard JSON in CI fixture; new "Add Postgres" wizard produces a working dashboard in < 60s. |
| 13 | Fresh VM → host + journald → workspace dashboard in 90s with no config edits; live debugger explains a deliberately misrouted metric. |
| 14.1 | Alert fires → notification includes runbook → ack flow walks all steps → MTTR-by-runbook chart renders. |
| 14.2 | "show me checkout p99 for the last hour" returns valid PromQL on a seeded fixture; reject rate < 30% in dogfood. |
| 14.3 | Synthetic cardinality explosion → portal surfaces it → one-click drop label → series count drops within 60s. |
| 14.5 | Round-trip: create dashboard in UI → export TF → destroy → apply TF → identical UUID + content. |

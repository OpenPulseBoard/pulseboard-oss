# PulseBoard — Plan to a Commercial Observability Platform

> Status: **planning** · Hosting model: **SaaS-only** · Last revised: 2026-05-21

Take the PulseBoard demo (single-binary F#/Suave app with in-memory ring +
16-byte segment files, WS hub, threshold alerts, Basic-Auth ingest) and grow
it into a SaaS positioned against Grafana Cloud / Datadog / New Relic /
Honeycomb.

**Approach.** Don’t rewrite storage and query engines. Embed best-in-class
OSS (Mimir/VictoriaMetrics for metrics, Loki/ClickHouse for logs,
Tempo/Jaeger for traces, Grafana OSS for dashboards) and concentrate
proprietary engineering on the **edge, control plane, multi-tenancy, alert
& notify pipeline, billing, UX, and pricing** — areas where commercial
fights are actually won and where the existing F# codebase already has
momentum.

Phases 1–3 = MVP / closed beta. Phases 4–6 = GA / scale. Phases 7–8 =
growth / moat.

---

## Decisions

| Topic                | Decision                                                                                          |
| -------------------- | ------------------------------------------------------------------------------------------------- |
| Hosting model        | **SaaS-only.** No paid on-prem, no BYOC. Single multi-region cloud deployment.                    |
| OSS edition          | Keep a free self-hosted OSS edition (hardened PulseBoard) as **lead-gen funnel only** — not sold. |
| Storage build vs buy | **Buy** — embed Mimir/Loki/Tempo. Revisit only if margins or perf demand owning the engine.       |
| Language             | F#/Suave for edge & control plane. Go for embedded storage tier (third-party). Polyglot is fine. |
| Pillar order         | Metrics-first GA → logs → traces.                                                                 |
| Out of scope (now)   | RUM, session replay, synthetic monitoring, continuous profiling, eBPF agents. Defer past Phase 8. |

Open items needing a steer (see [Open questions](#open-questions)): target
customer segment, regions for launch, free-tier generosity.

---

## Phase 1 — Foundations (control plane + multi-tenancy)

Goal: every byte that enters the system is owned by a tenant; ops can run
many tenants on shared infra without leakage.

1. **Tenant model.** Postgres tables `tenants`, `users`, `memberships`,
   `api_keys`, `roles`. A `TenantCtx` is injected into every WebPart
   pipeline by extending [Auth.fs](src/edge/Auth.fs).
2. **Identity.** Replace Basic-Auth-only with: scoped API keys
   (ingest/query/admin), OIDC SSO (Google/Microsoft/Okta) for the UI,
   short-lived JWT for browser sessions. Basic-Auth stays as a
   `--single-tenant` mode for the OSS funnel build.
3. **RBAC.** Roles `viewer`, `editor`, `admin`, `billing` enforced at the
   WebPart boundary; every check recorded in the audit log. *Parallel with
   step 2.*
4. **Audit log.** Append-only Postgres table + nightly S3 export. Every
   mutating call (rule create/delete, token issue, dashboard change)
   records `who / when / what / from-ip`.
5. **Quotas & rate limits.** Per-tenant: ingest rps, active series
   cardinality, log GiB/day, alert eval rps. Token-bucket middleware at
   the edge; 429 with `Retry-After`.
6. **Org/admin UI shell.** React/SolidStart frontend (served from a CDN,
   Suave handles the API). Login, org switcher, members, API keys, usage.

**Decision:** Postgres = metadata system-of-record only. All
metric/log/trace data lives in the dedicated TSDB/log-store/trace-store.

---

## Phase 2 — Ingestion at the edge (compatibility)

Goal: customers can point existing OTel collectors, Prometheus
`remote_write`, and Loki/Fluent agents at us without changing
instrumentation.

1. **OTLP receiver** (metrics + logs + traces). HTTP/JSON and
   HTTP/protobuf first; gRPC later. Hand-written WebParts on top of
   `Google.Protobuf`.
2. **Prometheus `remote_write`.** Decode snappy-framed protobuf; translate
   to the internal Point format. *Parallel with OTLP.*
3. **Prometheus scrape mode.** Tenant-defined scrape configs → background
   worker fans out HTTP GETs and writes through the same ingest path.
4. **Loki push API** (`/loki/api/v1/push`) for log compatibility — NDJSON
   and snappy-protobuf.
5. **StatsD UDP + Carbon plaintext TCP** for legacy stacks. Deferrable
   to Phase 4 if backlog is heavy.
6. **Edge gateway split.** ✅ DONE. `--role=edge|storage|all` (default
   `all`). Edge tier authenticates, validates, applies quotas, then
   forwards to storage over an HMAC-signed protobuf internal protocol
   (`/_internal/v1/{metrics,logs,trace-count}`); 3-attempt retry with
   100/200/400ms backoff, single shared `HttpClient`. All six receivers
   (Ingest, PromRemoteWrite, Otlp metrics/logs/traces, LokiPush,
   PromScrape, UDP/TCP Listeners) route through `IStorageClient`
   (`Gateway.fs`). Constant-time HMAC compare; 401 on missing/bad sig.
   **Deferred:** edge process still allocates the in-memory
   MetricStore/hub/alert engine (they sit idle); a follow-up will skip
   them for zero-overhead edge.

**Reuse.** Keep [Ingest.fs](src/edge/Ingest.fs) `JsonDocument` path as the
"PulseBoard native" format for the demo SDK; new receivers compose
alongside it under the same `pathStarts` gates.

---

## Phase 3 — Storage that scales beyond one box

The current 16-byte segment writer in [Segments.fs](src/edge/Segments.fs) is fine
for demo, fatal for production (no compression, no compaction, no
downsampling, no cardinality control, no HA).

**Plan: embed best-in-class OSS engines.**

| Pillar  | Engine                                  |
| ------- | --------------------------------------- |
| Metrics | **Grafana Mimir** (or VictoriaMetrics)  |
| Logs    | **Grafana Loki** (or ClickHouse)        |
| Traces  | **Grafana Tempo** (or Jaeger+ClickHouse) |

We own: the edge, the control plane, the alert engine, the notify
pipeline, the UI, billing.

1. **Pluggable storage backend.** ✅ DONE. `Storage.fs` defines
   `IMetricBackend` / `ILogBackend` / `ITraceBackend`; `Embedded*Backend`
   wraps today's in-process stores. `CloudBackends.fs` adds real HTTP
   impls: `MimirMetricBackend` (Prometheus remote_write 1.0, snappy +
   protobuf, `X-Scope-OrgID`), `LokiLogBackend` (JSON `/loki/api/v1/push`),
   `TempoTraceBackend` (OTLP/HTTP passthrough on `/v1/traces` via the
   new `IRawTraceBackend` extension that `Otlp.traces` forwards raw
   bytes through). Selection is per-pillar via
   `--mimir-url=` / `--loki-url=` / `--tempo-url=` (plus optional
   `--*-bearer=` and `--*-org-header=`); without those flags every
   pillar stays embedded so the OSS demo keeps booting with zero config.
2. **Cardinality control.** ✅ DONE. Receivers (Ingest / PromRemoteWrite /
   Otlp / PromScrape / Listeners) already call `Limiter.TryAdmitSeries`
   and surface `rejectedCardinality` to the caller. `EmbeddedMetricBackend`
   now also enforces admission on the backend layer (defense-in-depth
   for the edge→storage HTTP path) and tracks a per-tenant
   `droppedSamples` counter, surfaced at
   `GET /api/admin/tenants/<id>/cardinality` as
   `{seriesCount, droppedSamples, cap, capOverridden}`.
3. **Retention policies.** ✅ DONE. `Retention.fs` adds
   `RetentionPolicy` (per-pillar TTL in ms: metrics / logs / traces),
   a `RetentionStore` with system defaults + per-tenant overrides
   (Postgres-backed via `PgRetentionOverrides.fs`, in-memory
   otherwise), and an `EmbeddedCompactor` that walks the in-process
   `MetricStore` / `LogStore` on a timer and prunes anything older
   than the most-generous configured horizon (so no tenant's data is
   evicted earlier than its policy allows; embedded stores are
   process-global). Pillars swapped to a cloud backend skip the
   compactor — Mimir / Loki / Tempo enforce TTL upstream. Wired via
   `--retention-metrics-ms=` / `--retention-logs-ms=` /
   `--retention-traces-ms=` / `--retention-compact-interval-ms=`
   (default 60s) and `PULSE_RETENTION_*` envs. Admin endpoints
   `GET` / `PUT /api/admin/tenants/<id>/retention` mutate per-tenant
   overrides (numbers set, `null` clears, missing fields untouched).
   Object-store lifecycle rules are out of scope until we ship cold
   tiering.
4. ✅ **Downsampling rollups** (1m / 5m / 1h). New
   [`Rollups.fs`](src/edge/Rollups.fs) module:
   `Resolution` enumerates the three bucket widths; `Bucket` carries
   `{ts; count; min; max; sum}` so any of avg/min/max/sum/count can be
   served without keeping raw points; `RollupStore` holds a thread-safe
   per-`(metric, resolution)` bucket array capped at
   `maxBucketsPerSeries=10_000`; `RollupWorker` runs an async loop that
   every `--rollups-interval-ms=` (default 30000ms) snapshots every
   metric in `MetricStore`, re-aggregates into each resolution, and
   wholesale-replaces the bucket arrays (idempotent, partial buckets
   correct). [`Query.fs`](src/edge/Query.fs) extended:
   `GET /api/metrics/<name>?sinceMs=…&step=<ms|auto|raw>&agg=avg|min|max|sum|count`;
   when `step` is `auto` (default) the resolution is picked from the
   window length — `<1h`: raw, `<12h`: 1m, `<7d`: 5m, `≥7d`: 1h.
   [`Program.fs`](src/edge/Program.fs) wires
   `--rollups-enabled=` / `--rollups-interval-ms=` + `PULSE_ROLLUPS_*`
   envs and only constructs the worker when metrics are still embedded
   (skipped when `--mimir-url=` is set, since Mimir handles its own
   recording rules / blocks compactor). Smoke: 10 raw points produced
   a 1m bucket with `avg=0.46 max=0.9 count=10`; a 24h window auto-
   selected 5m resolution; unknown `step=` falls back to raw.

---

## Phase 4 — Query, dashboards, exploration

1. **Query API.** ✅ DONE — [QueryApi.fs](src/edge/QueryApi.fs) exposes
   `/api/prom/api/v1/{query,query_range,labels,label/<n>/values,series}`
   and `/api/loki/api/v1/{query_range,labels,label/<n>/values}`.
   When `--mimir-url=` / `--loki-url=` are set the requests are
   forwarded verbatim (method, raw query, body, content-type) to the
   upstream's `/prometheus/api/v1/*` / `/loki/api/v1/*` surface with
   the tenant's id injected as the configured org header (default
   `X-Scope-OrgID`) and an optional bearer token. Without an upstream
   we serve an embedded subset on the local `MetricStore` / `LogStore`
   / `RollupStore`: PromQL vector selectors only
   (`metric{label="..."}`, regex matchers, `!=` / `!~`) — anything
   compound returns `bad_data` with a hint to set `--mimir-url=`.
   `query_range` uses the rollup buckets when `step` matches a known
   resolution (1m / 5m / 1h) so dashboards get pre-aggregated points
   for free. LogQL accepts `{service="..."}` with an optional `|=` /
   `!=` line filter; everything else (parser pipeline, metric
   queries, json/logfmt extractors) requires a real Loki via
   `--loki-url=`. The native query DSL in [Query.fs](src/edge/Query.fs)
   stays the simple API surface for the dashboard SPA.
2. **Dashboards.** Embed Grafana OSS first (SSO bridge, we provide
   datasources). In-house dashboard v2 only after PMF.
3. **Explore view** for ad-hoc query + log tail — extend the existing
   dashboard SPA in [wwwroot/index.html](src/edge/wwwroot/index.html).
4. **Service map / RUM stubs** once traces land.

---

## Phase 5 — Alerting & on-call (commercial-grade)

Today: [Alerts.fs](src/edge/Alerts.fs) evaluates a hardcoded `cpu > 0.9 for 30s`
rule; [Notify.fs](src/edge/Notify.fs) fans out to webhook + Slack with no
retry/dedup.

1. **Rule engine.** Persisted PromQL/LogQL rule groups per tenant;
   evaluator pool with rule-group sharding; expose `pulse_rule_eval_seconds`.
2. **Alertmanager-equivalent.** Routing tree (matchers → receivers),
   grouping window, dedup, inhibition, **silences**, time-based mutes.
3. **Receivers.** Beyond today’s Slack/webhook: PagerDuty, Opsgenie,
   Microsoft Teams, Discord, email (SES/SendGrid), SMS (Twilio), Jira
   ticket, HMAC-signed webhooks (SHA-256 over body with per-receiver
   secret).
4. **On-call schedules & escalation policies.** Rotations, overrides,
   handoffs, acknowledgement, re-notify.
5. **Notify pipeline reliability.** Persistent outbound queue (Postgres
   or NATS JetStream) with retry / backoff / dead-letter. Today’s
   fire-and-forget `Notify.postJson` is replaced by enqueue-then-worker.

---

## Phase 6 — Reliability, security, compliance

1. **HA topology.** Edge tier behind LB; storage tier replicated (Mimir
   RF=3); Postgres HA (Patroni or RDS Multi-AZ); regional failover runbook.
2. **TLS everywhere.** Terminate at the LB; mTLS between edge and
   storage; cert-manager rotation.
3. **Secrets.** Vault or AWS Secrets Manager for tenant API keys
   (Argon2id-hashed at rest), receiver credentials, signing keys.
4. **Encryption at rest.** Object store SSE-KMS; Postgres TDE; per-tenant
   data keys for sensitive log fields (envelope encryption).
5. **Compliance program.** SOC 2 Type II first (12-month runway), then
   HIPAA BAAs, GDPR DPA, ISO 27001. Annual pen-test.
6. **Self-observability.** Emit own metrics/logs/traces into a dedicated
   meta-tenant; dashboards + SLOs (`ingest_success_ratio > 99.9%`,
   `query_p99_latency < 1s`).

---

## Phase 7 — Commercial surface (SaaS-only)

1. **Billing.** Stripe-backed metered billing: ingested GiB, active
   series, log GiB, trace spans, alert evals, seats. Daily aggregation
   job → Stripe usage records. Hard caps + soft caps + overage emails.
2. **Plans.**
   - **Free** — generous OSS-friendly limits, community support.
   - **Pro** — per-seat + usage; SSO via Google/Microsoft.
   - **Enterprise** — custom contract, SAML, audit export, BYOK, SLA.
   *(Hosting is always our cloud — Enterprise = bigger limits + SLA,
   never on-prem.)*
3. **Onboarding.** Self-serve signup → org provisioning → "send your
   first metric" wizard with copy-paste snippets for Node/Python/Go/Java/
   OTel/Prom/Docker/K8s.
4. **Marketplace integrations.** AWS CloudWatch, GCP Operations, Azure
   Monitor, GitHub Actions, Vercel, Render, Fly, Heroku — one-click installs.
5. **Docs site + API reference** (auto-generated from OpenAPI). Status
   page. Public roadmap.
6. **Support tooling.** Audited tenant impersonation, in-app chat
   (Intercom), incident comms.

---

## Phase 8 — Differentiation / moat

Pick 2-3 to over-invest in vs. competing flat.

1. **Cost transparency.** Real-time per-team cost attribution +
   cardinality explorer ("this label is costing $X/mo"). Datadog’s
   weakest spot.
2. **OSS self-hosted funnel.** Hardened PulseBoard as a polished, MIT/AGPL
   open-source edition. Lead-gen, not a sold product.
3. **Native AI assist.** "Why did p99 spike?" using exemplar traces + log
   correlation + LLM summarization over the tenant’s own data (private
   inference).
4. **Sub-second alerts.** Our WS hub already proves the live path —
   productize it for SLO burn-rate alerts; sell on time-to-detect where
   incumbents are minutes-late by design.
5. **Predictable pricing.** Flat tiers + public calculator. Bill-shock
   is industry-wide pain.

---

## Relevant existing code (informs Phase 1-2 hand-off)

- [src/edge/Program.fs](src/edge/Program.fs) — wiring point that becomes "edge service"
  main: CLI flag parsing, store/hub/timer wiring, `choose` composition.
- [src/edge/Auth.fs](src/edge/Auth.fs) — `protect` WebPart pattern is the hook for
  tenant-aware auth; the `TokenMap` becomes a `TenantResolver`.
- [src/edge/Ingest.fs](src/edge/Ingest.fs) — `JsonDocument` template; OTLP / Prom-RW
  receivers slot in next to it under `pathStarts`.
- [src/edge/Segments.fs](src/edge/Segments.fs) — becomes the `embedded` impl behind
  `IMetricBackend`; cloud edition swaps in a Mimir client.
- [src/edge/Alerts.fs](src/edge/Alerts.fs) — `Engine` → per-tenant rule-group evaluator;
  threshold-only `Rule` grows into PromQL expressions with rule-group
  sharding.
- [src/edge/Notify.fs](src/edge/Notify.fs) — `Sink` / `fanout` abstraction generalizes to
  "receivers" + routing tree; `postJson` → durable queue + worker.
- [src/edge/Hub.fs](src/edge/Hub.fs) — keep as the live-tail WebSocket layer; productize
  for sub-second alert push.

---

## Verification per phase

| Phase | Acceptance test                                                                                                                                                       |
| ----- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| P1    | Two tenants signed up. Fuzzed token swaps prove zero cross-tenant leak. Quotas return 429. Audit log captures every mutation.                                         |
| P2    | Unmodified OTel Collector + Prometheus `remote_write` point at the edge; data is queryable. OpenTelemetry compliance suite passes.                                    |
| P3    | 10k series × 10k samples/s sustained on 3-node Mimir behind the edge; kill any node, no data loss. Cardinality limiter rejects a runaway label.                       |
| P4    | Grafana dashboard with our datasource shows live metrics and logs side-by-side. PromQL parity tests pass against a reference suite.                                   |
| P5    | Simulate flapping alert → routing tree groups into one notification; silence suppresses; PagerDuty + Slack both fire; receiver outage → retry then DLQ.               |
| P6    | Chaos test kills storage replica → no query errors > 1s; SOC 2 readiness audit clean; TLS scan A+; secrets rotation drill green.                                      |
| P7    | End-to-end signup → ingest → upgrade to Pro → Stripe test-mode invoice generated with correct metered usage.                                                          |
| P8    | Ship one moat feature; measure delta in trial-to-paid conversion vs. control cohort.                                                                                  |

---

## Open questions

1. **Target customer segment** — Mid-market SaaS engineering teams
   (10-200 eng) / Enterprise platform teams / Indie devs & small
   startups. Plan currently assumes mid-market; pricing model and SSO
   priority shift per choice.
2. **Launch regions** — Single region (us-east-1) at GA, then EU?
   Multi-region from day one (cost + complexity)? Data-residency
   requirements drive Phase 6 timeline.
3. **Free-tier generosity** — Cheap acquisition vs. fixed COGS floor.
   Concrete numbers needed before Phase 7 build.
4. **OSS edition license** — MIT (max adoption, weakest moat),
   Apache-2.0, AGPL (forces hosted competitors to share), BSL with
   delayed open-sourcing à la Sentry. Decision drives community
   strategy.

---

## Repository extraction checklist (next step)

To move PulseBoard into its own tree + GitHub repo:

1. Decide repo name + org (suggest `pulseboard/pulseboard`).
2. Copy `examples/PulseBoard/` to the new repo root as `src/edge/`
   (rename to make room for `src/storage/`, `src/control-plane/`,
   `infra/`, `docs/`, etc.).
3. Vendor or paket-publish the small slice of Suave we actually use, or
   pin to upstream Suave 3.x via NuGet — drop the in-tree
   `ProjectReference` to `../../src/Suave/Suave.fsproj`.
4. Add: `LICENSE`, `README.md`, `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`,
   `SECURITY.md`, `.github/workflows/ci.yml` (dotnet build/test +
   lint), Dependabot config, issue + PR templates.
5. Carry this `PLAN.md` to the repo root.
6. Stand up project board mirroring the phases above.

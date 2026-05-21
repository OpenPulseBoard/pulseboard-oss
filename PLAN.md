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
2. **Dashboards.** ✅ DONE — in-house (no Grafana embed). Per-tenant
   CRUD store in [Dashboards.fs](src/edge/Dashboards.fs) (file-backed
   JSON at `<dataDir>/dashboards/<tenant>/<id>.json`, in-memory cache,
   atomic-ish writes, auto-seeded `overview` default for empty tenants).
   REST surface gated by the existing Query quota: `GET /api/dashboards`,
   `POST /api/dashboards` (server assigns a fresh id), `GET/PUT/DELETE
   /api/dashboards/<id>` (PUT preserves `createdAt`, stamps `updatedAt`).
   Single-tenant mode uses synthetic `TenantId "__local__"`.
   Frontend ([wwwroot/index.html](src/edge/wwwroot/index.html)) is a
   zero-build vanilla-JS SPA with a tabbed shell (Dashboards / Explore),
   12-column CSS grid, drag-to-move + drag-to-resize panels (edit mode),
   right-side editor drawer (title / panel type / query lang / expression
   / w·h / options / live preview), time-range + auto-refresh pickers,
   and uPlot ([wwwroot/uPlot.iife.min.js](src/edge/wwwroot/uPlot.iife.min.js),
   ~50 KB MIT, vendored) for time-series charts. Panel types:
   `timeseries`, `stat`, `logs`, `table`; query languages: `promql`
   (proxied to embedded `/api/prom/api/v1/query_range`), `logql`
   (`/api/loki/api/v1/query_range`), `native` (`/api/metrics/<n>`).
   The legacy real-time view is preserved at [`/live`](src/edge/wwwroot/live.html).
3. **Explore view** ✅ DONE alongside #2 — same SPA, second tab.
   Free-form query input with PromQL / LogQL / native picker, range
   selector (5m / 15m / 1h / 6h), Cmd/Ctrl-Enter to run, results render
   as uPlot chart or log list depending on result shape. Live-tail
   beyond static range still uses the legacy [`/live`](src/edge/wwwroot/live.html) WS view.
4. **Service map / RUM stubs** ✅ DONE.
   *Spans.* [src/edge/Spans.fs](src/edge/Spans.fs) introduces a first-class
   `Span` model (32-char hex `traceId`, 16-char hex `spanId`,
   `parentSpanId`, `service`, `operation`, `kind`, `startMs`/`endMs`,
   OTLP status, attribute map) and an `InMemorySpanStore` — a
   per-tenant bounded ring (default 10 000 spans, oldest dropped in
   10 % bulk evictions to amortise) that snapshots under a per-tenant
   lock so reads never block ingest. We pointedly do *not* introduce a
   new disk format here: production deployments still send raw OTLP to
   Tempo via the existing `--tempo-url=` passthrough; this store is a
   hot cache that powers the UI between restarts. The store also
   exports `PruneOlderThan` so the existing retention compactor can
   evict on the same schedule as metrics/logs.
   *OTLP decoder.* [src/edge/Otlp.fs](src/edge/Otlp.fs) gains a real
   span decoder (`decodeSpans`) that walks the `ExportTraceServiceRequest`
   protobuf tree (`ResourceSpans` → `ScopeSpans` → `Span`), hex-encodes
   the 16-byte `traceId` / 8-byte `spanId` / `parentSpanId`, lifts
   `service.name` from resource attributes (default `"unknown"`),
   converts `fixed64` nanosecond timestamps to milliseconds, and
   merges resource + span attributes into the span's attribute map.
   The `traces` handler keeps its existing two duties — increment
   `IStorageClient.IncTraceCount` for billing and (optionally) forward
   the raw protobuf to Tempo — and now also calls
   `ISpanStore.Ingest` so the UI sees the same data the upstream does.
   A `try/with` falls back to the legacy span counter if structured
   decoding ever fails, so billing keeps working on a partially
   corrupt payload.
   *Service map aggregation.* [src/edge/Spans.fs](src/edge/Spans.fs)
   builds the map at query time from a span snapshot: index spans by
   `(traceId, spanId)`, walk each span, accumulate per-service latency
   samples + error counts (nodes), and follow each `parentSpanId` to a
   parent span — when parent and child live in different services we
   credit an edge `parent.service → child.service` with the child's
   duration and error flag. Per-node and per-edge percentiles (p50 /
   p95 / p99) are computed by sorting the duration array and indexing
   — O(n log n) on a 10k ring is trivial. Spans without a resolvable
   parent or with a same-service parent never contribute to edges, so
   internal helper spans don't pollute the graph.
   *REST surface.* [src/edge/TraceApi.fs](src/edge/TraceApi.fs) mirrors
   `Dashboards.fs`'s `withTenant` + `Utf8JsonWriter` pattern and
   exposes three routes under the existing `/api/` auth gate:
   `GET /api/traces?sinceMs=&windowSec=&limit=` returns recent
   `TraceSummary` records sorted by `startMs` desc (default last hour,
   max 1000); `GET /api/traces/<traceId>` returns `{summary, spans[]}`
   sorted by `startMs` (404 when the store has no spans for that id);
   `GET /api/servicemap?sinceMs=&windowSec=` returns
   `{nodes[], edges[], sinceMs, generatedMs}` derived from the same
   snapshot logic. All three routes inherit the Query quota and audit
   gate.
   *RUM beacon.* [src/edge/Rum.fs](src/edge/Rum.fs) exposes
   `POST /rum/v1/events` in single-tenant mode and
   `POST /rum/v1/<tenantId>/events` in multi-tenant mode (deliberately
   unauthenticated — browsers can't safely carry server-side API
   keys; this is a stub, a real deployment would validate against a
   published-client-key registry). Bodies are JSON
   `{sessionId, url, userAgent, service?, events:[...]}` (a bare array
   body is also accepted). Each event is translated into the
   primitives PulseBoard already stores: `web_vital` → a metric named
   `rum_<name>_ms` (or `rum_cls` for the unitless CLS score),
   `page_load` → `rum_page_load_ms`, `error`/`exception` → a log line
   (`service=rum/<tenantOrLabel>`, `level=error`, message + optional
   stack) plus a `rum_errors_total` counter, `custom` →
   `rum_custom_<sanitised_name>`. Because everything lands in
   `MetricStore` and `LogStore`, dashboards / alerts / Prometheus
   query proxy / retention all just work without any further
   plumbing. CORS preflight is handled (`Access-Control-Allow-Origin:
   *`) so dev pages can post beacons from any origin, and bodies
   over 64 KiB return 413.
   *SPA.* [src/edge/wwwroot/index.html](src/edge/wwwroot/index.html)
   gains two new tabs alongside Dashboards + Explore: **Traces**
   (range-pickered table of trace summaries — click a row to open a
   modal waterfall sorted by span start, each span rendered as a
   horizontal bar coloured by a stable per-service hue, with hover
   tooltips and a top-of-modal summary line), and **Service Map**
   (SVG with services laid out on a circle, edges drawn as arrows
   whose stroke width scales with call volume and whose hue scales
   with error rate green→red, with hover tooltips showing p50/p95/p99
   per node and edge). Both tabs share the same range-picker UX as
   the rest of the SPA and are wired into the existing hash router
   (`#/traces`, `#/map`).
   *Wiring.* [src/edge/Program.fs](src/edge/Program.fs) constructs
   the singleton `InMemorySpanStore` early enough to thread into both
   `Otlp.traces` and `TraceApi.webPart`, and mounts `Rum.webPart`
   outside the query auth gate (alongside `/ingest/*` and
   `/v1/{metrics,logs,traces}`) so beacons reach it without an API
   key. Startup banner now lists the span store capacity, the RUM
   beacon URL, and the three new query routes.

---

## Phase 5 — Alerting & on-call (commercial-grade)

Today: [Alerts.fs](src/edge/Alerts.fs) evaluates a hardcoded `cpu > 0.9 for 30s`
rule; [Notify.fs](src/edge/Notify.fs) fans out to webhook + Slack with no
retry/dedup.

1. ✅ **DONE — Rule engine** ([Rules.fs](src/edge/Rules.fs)).
   Persisted PromQL/LogQL rule groups per tenant under
   `<dataDir>/rules/<tenant>/<groupId>.json`. Each group has its own
   `intervalMs`; rules carry `name`, `expr`, `cmp`, `threshold`, `forMs`,
   `severity` (info/warning/critical/page), `labels`, `annotations`. The
   evaluator runs a worker pool sized at `max(2, ProcessorCount/2)`,
   shards groups by `hash(groupId) mod workerCount`, and re-evaluates
   each group on its own cadence. PromQL rules walk metric series and
   compare the last sample; LogQL rules count matches in the log ring
   within `forMs`. Pending→Firing happens after `forMs` of sustained
   breach; Resolved is emitted automatically when the breach clears.
   Each rule evaluation is timed and recorded as
   `pulse_rule_eval_seconds` so PulseBoard can monitor its own alerting
   loop. REST: `GET/POST /api/rules`, `GET/PUT/DELETE /api/rules/<id>`,
   `GET /api/alerts` (active fingerprints with state, value, labels).
   The legacy `Engine` in [Alerts.fs](src/edge/Alerts.fs) and its
   hard-coded `cpu-high` rule are gone — `seedIfEmpty` plants the same
   rule into the new store on first boot so out-of-the-box behaviour is
   preserved.

2. ✅ **DONE — Alertmanager-equivalent** ([Routing.fs](src/edge/Routing.fs)).
   Per-tenant config persisted as a single JSON document at
   `<dataDir>/routing/<tenant>.json`. The shape mirrors Prometheus
   Alertmanager: a recursive `route` tree (matchers with `=`/`!=`/`=~`/
   `!~`, `groupBy`, `groupWaitMs`, `groupIntervalMs`,
   `repeatIntervalMs`, `continue`, `muteTimeIds`, child `routes`); an
   array of `receivers` (`webhook`, `slack`, `hmac_webhook`,
   `pagerduty`, `opsgenie`, `teams`, `discord`); `silences` (matcher
   set + `startsAt`/`endsAt` + `createdBy`/`comment`); `inhibitions`
   (source matchers suppress target matchers when `equal` labels
   match); and `muteTimes` (weekday bitmask + minute-of-day window in
   UTC). The `Pipeline` is an `IAlertSink`: on each firing alert it
   checks silence → mute → inhibition, walks the route tree (respecting
   `continue`), then bookkeeps a per-`(receiver, groupKey)` group that
   waits `groupWaitMs` before first send and `groupIntervalMs` between
   follow-ups; identical fingerprint sets within `repeatIntervalMs` are
   deduped. A 1-second timer flushes due groups by serialising
   `{receiver, groupKey, ts, alerts:[...]}` envelopes onto the notify
   queue. Counters: `pulse_alerts_routed_total`,
   `pulse_alerts_silenced_total`, `pulse_alerts_muted_total`,
   `pulse_alerts_inhibited_total`, `pulse_alerts_resolved_total`. REST:
   `GET/PUT /api/alertmanager/config`, `GET/POST /api/silences`,
   `DELETE /api/silences/<id>`. Existing `--webhook=` / `--slack=` URLs
   are lifted into receivers in the seeded default config so single-
   tenant operators keep the same out-of-the-box delivery.

3. ✅ **DONE — Receivers** ([NotifyQueue.fs](src/edge/NotifyQueue.fs)).
   The dispatcher now does per-receiver HTTP shaping in a single
   `shapeRequest` helper, so each `OutboundMessage` carries an `extra`
   string→string map alongside the JSON envelope and the transport
   layer reshapes URL, body, content-type, and auth headers based on
   `receiverType`:
   - `webhook` / `slack` / `teams` / `discord` — JSON envelope as-is.
   - `hmac_webhook` — `X-PulseBoard-Signature: sha256=<hex>` over the
     UTF-8 body using the receiver secret.
   - `pagerduty` — `Authorization: Token token=<key>` (Events API v2
     compatible; `routing_key` lives in the envelope).
   - `opsgenie` — `Authorization: GenieKey <key>`.
   - `sendgrid` — rebuilds the body as SendGrid v3 mail JSON using
     `extra.from` / `extra.to` and a human-readable subject + plain-
     text summary rendered from the envelope; `Authorization: Bearer
     <secret>`; defaults to `https://api.sendgrid.com/v3/mail/send`.
   - `twilio` — form-encoded `From`/`To`/`Body` (summary capped at
     1500 chars), HTTP Basic auth with `extra.account_sid : <secret>`;
     defaults to `/2010-04-01/Accounts/<sid>/Messages.json`.
   - `jira` — POSTs an Atlassian Document Format issue to
     `<url>/rest/api/3/issue` using `extra.project` / `extra.issueType`
     and HTTP Basic auth with `extra.user : <secret>`.
   - `ses` — SES Query API form body (Source/Destination/Subject/Body)
     for use behind an IAM-authenticated proxy that attaches SigV4.
   Operator-supplied per-receiver headers always win, so a receiver
   can layer custom headers on top of any of the shapings above.

4. ✅ **DONE — On-call schedules & escalation policies**
   ([OnCall.fs](src/edge/OnCall.fs)). A per-tenant catalog of `users`,
   `schedules`, and `policies` is persisted as a single JSON document
   under `<dataDir>/oncall/<tenant>.json` via `FileCatalogStore`; an
   append-only ack journal lives at `<dataDir>/acks/<tenant>.ndjson`
   via `FileAckStore` with an in-memory `HashSet` of acked fingerprints
   for O(1) suppression lookups. Schedules are made of round-robin
   `Rotation`s (members, periodMs, startAt) plus point-in-time
   `ScheduleOverride`s (overrides win when active). Escalation
   policies declare an ordered list of `EscalationStep { delayMs;
   targets }` where each `Target` is one of `TgtReceiver`,
   `TgtUser`, or `TgtSchedule` — user and schedule targets resolve
   to the user's declared `receiverIds`, with schedule targets first
   resolving to whoever is on call right now via `whoIsOnCall`.
   `Route` gained a `policyId : string option` field: when a routed
   group has a policy set, the `Pipeline.flushDue` loop bypasses the
   normal `groupWait`/`groupInterval` cadence and instead walks the
   policy's steps — step `k` fires once `now - anchor >= step.delayMs`
   (anchor = `firstSeenAt` for step 0, otherwise the previous step's
   send time), enqueueing one envelope per receiver returned by
   `Escalator.ResolveStep`. An ack on any fingerprint in the group
   halts further escalation until the alert resolves and a fresh
   outbreak begins. Pipeline gained a `SetEscalator(IEscalator)` hook
   so the `Routing` module stays free of any on-call dependency; the
   adapter lives in `OnCall.Escalator`. Self-metrics:
   `pulse_escalation_step_total`, `pulse_alerts_acked_total`. REST:
   `GET/PUT /api/oncall/catalog`, `GET /api/oncall/whoison/<scheduleId>`,
   `POST /api/alerts/<fp>/ack`, `GET /api/alerts/<fp>/acks`.

5. ✅ **DONE — Notify pipeline reliability** ([NotifyQueue.fs](src/edge/NotifyQueue.fs)).
   Persistent outbound queue at `<dataDir>/notify/queue.ndjson`
   (append-only NDJSON journal with tombstone lines, compacted when
   tombstones exceed 50% of journal lines) plus a sibling
   `dead.ndjson`. Each `OutboundMessage` carries `id`, `tenantId`,
   `receiverId`, `receiverType`, `url`, `secret`, `body`, `headers`,
   `attempt`, `maxAttempts` (5), `enqueuedAt`, `nextRunAt`, `lastError`.
   The store replays the journal on startup so in-flight messages
   survive crashes. Two dispatch workers per process lease ready
   messages (a per-store `HashSet<string> leased` prevents double-
   lease during in-flight HTTP), POST with receiver-specific headers,
   and on failure schedule a retry at `baseBackoffMs * 2^attempt +
   rand(0, cap/4)` capped by `maxBackoffMs`; on `maxAttempts` the
   message is moved to the dead-letter file. Self-metrics:
   `pulse_notify_enqueued_total`, `pulse_notify_attempts_total`,
   `pulse_notify_failures_total`. REST: `GET /api/notify/queue`,
   `GET /api/notify/dlq`, `POST /api/notify/dlq/<id>/replay`,
   `DELETE /api/notify/dlq/<id>`. The old fire-and-forget
   `Notify.postJson` path is fully replaced — the `Pipeline` enqueues,
   workers dispatch.

---

## Phase 6 — Reliability, security, compliance

1. **HA topology.** Edge tier behind LB; storage tier replicated (Mimir
   RF=3); Postgres HA (Patroni or RDS Multi-AZ); regional failover runbook.

   📐 **DESIGNED (deployment architecture; no edge code changes).** The
   target topology is a single active region with a warm standby in a
   second region, all on Kubernetes. The edge process (this repo) is a
   stateless 12-factor service and scales horizontally; durable state
   lives outside it.

   **Layers, top to bottom:**

   - **Global edge (anycast / GeoDNS).** Cloudflare (or Route 53 +
     CloudFront) terminates TLS for `*.pulseboard.app` and forwards to
     the active-region public NLB. Health checks at
     `GET /api/healthz` (to be added — returns `200 {ok:true}` once
     storage adapters report ready) drive automatic failover to the
     standby region's NLB. TTL ≤ 30 s.
   - **Regional load balancer.** AWS NLB (or GCP TCP LB) in front of a
     Kubernetes Service of type `LoadBalancer`. Two listeners: 443 for
     ingest + query + admin, 9090 for the Prometheus remote_write
     fast path. Cross-zone load balancing on. Connection draining
     30 s. PROXY protocol v2 enabled so the edge sees real client IPs
     for rate-limit accounting.
   - **Ingress / TLS termination.** `ingress-nginx` (or Envoy via
     Contour) deployed as a DaemonSet across ≥ 3 AZs. Terminates
     external TLS, re-originates internal mTLS to the edge (see #2).
     Pod anti-affinity by `topology.kubernetes.io/zone` so a single
     AZ outage cannot drain the ingress pool.
   - **Edge tier (this F# service).** Deployment with
     `replicas >= 3` (HPA target: 60 % CPU, min 3, max 30),
     `topologySpreadConstraints` on `zone`, `PodDisruptionBudget`
     `minAvailable: 2`. Probes:
     - `readinessProbe` → `GET /api/readyz` — fails until tenant
       store, metric backend, and queue all answer; gates LB traffic.
     - `livenessProbe` → `GET /api/livez` — process-only, never
       depends on downstream stores (avoids cascading restarts).
     - `startupProbe` → 30-attempt grace for cold caches and Self
       bootstrap.
     The pod is single-container, runs as non-root, read-only root FS,
     `seccompProfile: RuntimeDefault`. The only writable volume is
     `/var/lib/pulseboard` (ephemeral `emptyDir` in stateless mode;
     EBS PVC only when local file-backed stores are deliberately
     enabled for dev / single-node deploys).
   - **Control plane store (Postgres).** Tenants, API keys (already
     Argon2id-hashed — Phase 6 #3), audit log, dashboards, alert
     rules, routing config, scrape targets, listener configs. Deployed
     as **RDS Aurora PostgreSQL Multi-AZ** in prod (writer + ≥ 2
     readers across AZs, automated failover ≤ 35 s). On-prem
     equivalent: **Patroni 3.x** with 3 PG nodes + 3 etcd nodes, sync
     replication to one replica (`synchronous_commit = on`,
     `synchronous_standby_names = 'ANY 1 (*)'`). PITR with 14-day
     window; nightly logical dump shipped to object store for
     bootstrap restore. Connection pool: **PgBouncer** (transaction
     mode) sidecar per edge pod, 25 conns/pod, hard cap server-side
     at `pool_max * replicas + 50` headroom.
   - **Metrics backend.** **Grafana Mimir** in microservices mode,
     **replication factor 3** across AZs (ingesters, store-gateways,
     compactors). Backed by S3 (or GCS) with SSE-KMS. Ingest path:
     edge → Mimir distributor over remote_write (already supported);
     query path: edge → Mimir query-frontend with split-by-interval
     + result caching (memcached). Tenant header
     `X-Scope-OrgID: <tenantId>` is set by the edge.
   - **Logs backend.** **Grafana Loki** in microservices mode, RF=3,
     boltdb-shipper + TSDB shipper on S3. Edge speaks the existing
     Loki push API on egress.
   - **Traces backend.** **Grafana Tempo**, RF=2 (traces tolerate
     lower RF), object-store backed. Edge forwards via OTLP/HTTP.
   - **Notify queue.** Redis (ElastiCache Multi-AZ) cluster mode
     enabled, RF=2; persistence AOF every-sec. The edge's existing
     file-backed `NotifyQueue` is swapped for a Redis-backed adapter
     in cloud deploys (interface already abstract, implementation TBD).
     Dead-letter list per receiver, alarm at depth > 1 000.
   - **Object store.** S3 (or GCS) — versioning on, lifecycle to
     Glacier after 90 days, SSE-KMS with a per-environment CMK.
     Holds Mimir/Loki/Tempo blocks, Postgres backups, KEK escrow
     (sealed copy of the cluster KEK for DR).

   **Regional failover.** Active/standby, **RPO ≤ 60 s, RTO ≤ 15 min.**
   Postgres uses cross-region read replicas (Aurora Global Database, or
   logical streaming for self-hosted Patroni). Mimir/Loki/Tempo use
   S3 Cross-Region Replication on the bucket. Standby region runs the
   edge tier at `replicas: 1` (warm), scaled out by the failover
   runbook. Promotion sequence is documented in
   [`infra/runbooks/regional-failover.md`](infra/runbooks/regional-failover.md).
   GeoDNS is the user-visible cutover; storage promotion happens
   first.

   **Single-AZ resilience.** Any one AZ may fail without user impact:
   - ingress, edge, Mimir/Loki/Tempo ingesters are spread across ≥ 3
     AZs;
   - Postgres failover is automatic;
   - Redis fails over via Sentinel/cluster mode;
   - object store is regionally durable by definition.

   **Capacity tiers.**

   | Tier   | Edge replicas | Mimir ing. | Loki ing. | Postgres        |
   |--------|---------------|------------|-----------|-----------------|
   | dev    | 1             | 1×RF1      | 1×RF1     | single t4g.medium |
   | stage  | 3             | 3×RF3      | 3×RF3     | Aurora 2 inst.  |
   | prod   | 6+ (HPA)      | 9×RF3      | 9×RF3     | Aurora 3 inst.  |

   **Edge-side prerequisites still to ship (small, tracked separately):**
   - `GET /api/healthz` / `/api/readyz` / `/api/livez` endpoints.
   - Redis-backed `INotifyQueue` adapter (file-backed remains for OSS).
   - Postgres connection-string env override is already supported via
     `--postgres=` (Phase 5).

2. **TLS everywhere.** Terminate at the LB; mTLS between edge and
   storage; cert-manager rotation.

   📐 **DESIGNED (deployment architecture; no edge code changes
   required for the OSS edge — TLS is terminated at ingress in cloud
   deploys).**

   **Trust zones.**

   - **Public zone.** `*.pulseboard.app`, `*.ingest.pulseboard.app`,
     `*.api.pulseboard.app`. Certs issued by **Let's Encrypt**
     (ACME DNS-01 via Route 53) using **cert-manager** with a
     `ClusterIssuer` per environment. Wildcard certs; 90-day lifetime,
     auto-renewed at T-30 days. TLS 1.3 only; TLS 1.2 allowed for
     ingest endpoints (some Prom/OTel collectors lag). HSTS
     `max-age=31536000; includeSubDomains; preload`. OCSP stapling on.
   - **Cluster-internal zone.** `*.svc.cluster.local`. A **private
     PKI** rooted at an offline (HSM-held) root CA issues a per-cluster
     intermediate that lives in cert-manager as a `CA` `ClusterIssuer`.
     Every pod-to-pod hop runs **mTLS** (see below). Internal cert
     lifetime 30 days, auto-renewed at T-7. SPIFFE-style identities
     `spiffe://pulseboard.internal/ns/<ns>/sa/<serviceAccount>`.
   - **Out-of-cluster managed services** (RDS, ElastiCache, S3). TLS
     to the managed endpoint with the cloud-provided CA bundle pinned
     in a `ConfigMap` and rotated via Renovate PRs.

   **mTLS topology.**

   ```
   client ──TLS(public)──► NLB ──TLS(public)──► ingress-nginx
       ingress-nginx ──mTLS(internal CA)──► edge
       edge ──mTLS(internal CA)──► PgBouncer ──TLS──► Postgres
       edge ──mTLS(internal CA)──► Mimir distributor
       edge ──mTLS(internal CA)──► Loki distributor
       edge ──mTLS(internal CA)──► Tempo distributor
       edge ──TLS+AUTH──► Redis (ElastiCache encryption in transit)
   ```

   The edge does not currently originate mTLS in code; in the target
   deploy this is handled transparently by a **per-pod sidecar
   (Linkerd or Istio in `STRICT` mTLS mode)**, so the edge keeps
   talking plain HTTP to `localhost` and the mesh upgrades the
   connection. This keeps the OSS edge mesh-agnostic and lets the
   self-hosted footprint stay sidecar-free.

   **Certificate lifecycle (cert-manager).**

   - `ClusterIssuer/letsencrypt-prod` → public certs, DNS-01.
   - `ClusterIssuer/pulseboard-internal-ca` → mTLS certs.
   - One `Certificate` per public hostname (renewed centrally at the
     ingress) and one per workload `ServiceAccount` for internal
     identities (renewed by cert-manager-csi-driver, mounted as a
     short-lived projected volume — no secrets-in-etcd).
   - Renewal alerts fire 14 days and 3 days before expiry via the
     existing alert pipeline.
   - **Annual root rotation drill** documented in
     [`infra/runbooks/tls-rotation.md`](infra/runbooks/tls-rotation.md);
     intermediate is rotated every 18 months with a 6-month overlap.

   **Cipher / protocol policy (Mozilla "intermediate", May 2026):**

   - Protocols: TLS 1.3 + TLS 1.2.
   - TLS 1.3 ciphers: `TLS_AES_128_GCM_SHA256`,
     `TLS_AES_256_GCM_SHA384`, `TLS_CHACHA20_POLY1305_SHA256`.
   - TLS 1.2 ciphers: ECDHE+AES-GCM and ECDHE+CHACHA20 only.
   - No RSA key exchange, no CBC, no SHA-1.
   - X25519 + secp384r1 curves.

   **BYO certs (Enterprise plan, Phase 7).** A tenant may bring a
   public-CA cert for a custom CNAME (e.g.
   `metrics.acme.com → acme.ingest.pulseboard.app`); cert-manager
   handles issuance via DNS-01 against the customer's delegated zone,
   plus a per-tenant `Ingress` with SNI routing. The admin REST surface
   (`POST /api/admin/tenants/<id>/domains`) is not yet implemented —
   tracked separately under Phase 7 onboarding.

   **OSS / self-hosted story.** For single-node OSS deploys, the edge
   speaks plain HTTP on `:5000` by default and is expected to live
   behind the user's own reverse proxy (Caddy, Traefik, nginx) which
   handles TLS. We ship a sample Caddyfile and a Compose stack in
   [`infra/docker/`](infra/docker/) (to be added) so a `caddy run`
   gets HTTPS via Let's Encrypt with one line. The edge itself does
   not need to grow a TLS listener in code — keeping that concern at
   the proxy layer matches the deployment guidance for both cloud and
   self-hosted users.

   **Edge-side prerequisites still to ship (small, tracked separately):**
   - Honor `X-Forwarded-For` / PROXY protocol for client IP in
     rate-limit accounting (currently uses the socket peer).
   - Surface `Strict-Transport-Security` and the rest of the standard
     security header set on every response (defense-in-depth even
     though ingress sets them too).
   - Document the cert-manager `ClusterIssuer` manifests in
     `infra/helm/` once Helm charts land.

3. **Secrets.** Vault or AWS Secrets Manager for tenant API keys
   (Argon2id-hashed at rest), receiver credentials, signing keys.

   ✅ **DONE (in-process Argon2id, persisted via existing tenant store).**
   Tenant API keys are now Argon2id-hashed at rest using
   `Konscious.Security.Cryptography.Argon2` (memory=64 MiB, time=3,
   parallelism=2, 32-byte tag, 16-byte salt). `Tenancy.argon2idTag` emits
   a self-describing identifier `argon2id:t=3,m=65536,p=2` written into
   the existing `hashAlgorithm` column; verifier dispatches on this
   string and falls back to PBKDF2-HMACSHA256 for legacy rows — no
   schema migration is required and existing keys keep working. New keys
   issued by `InMemoryTenantStore.IssueApiKey` and `PgTenantStore`
   take the Argon2id path automatically. A fixed-cost PBKDF2 branch
   remains for unknown-algorithm IDs to preserve a constant timing
   envelope. Vault / Secrets Manager integration for receiver creds and
   signing keys is still TODO and is out of scope for the OSS edge.

4. **Encryption at rest.** Object store SSE-KMS; Postgres TDE; per-tenant
   data keys for sensitive log fields (envelope encryption).

   ✅ **DONE (envelope encryption for log PII markers).** A new
   `Secrets.fs` module ships an envelope-encryption stack: a single
   32-byte KEK is loaded from `PULSE_MASTER_KEY` (base64, 32 bytes) or
   auto-generated at `<data>/secrets/master.key` (mode 0600 on Unix).
   Per-tenant 32-byte DEKs are AES-GCM-wrapped by the KEK and persisted
   as `<data>/secrets/<tenantId>.dek.json` with the on-disk envelope
   `{ "v":1, "nonce":"<b64url>", "ct":"<b64url>" }` (12-byte nonce,
   16-byte tag concatenated to the ciphertext). `FileSecretsStore`
   caches unwrapped DEKs in a `ConcurrentDictionary` and lazily creates
   them on first use. The wire token format for application ciphertexts
   is `enc:v1:<nonceB64Url>:<ctTagB64Url>` (URL-safe base64, no
   padding). The ingest path scans every log message for inline
   `[[pii:<value>]]` markers and replaces each occurrence with the
   tenant-scoped `enc:v1:...` token before storage — so dropping a
   marker around an email or SSN is enough to ship it encrypted, with
   no schema changes. A `FilePiiPolicyStore` persists a per-tenant
   string array of declared PII field names at
   `<data>/secrets/<tenantId>.pii.json` for future structured-field
   routing. The Admin-scoped REST surface is:
   - `GET /api/secrets/policy` → `{"fields":[...]}`
   - `PUT /api/secrets/policy` body `{"fields":[...]}` → `{"ok":true,"count":N}`
   - `POST /api/secrets/encrypt` body `{"plaintext":"..."}` → `{"ciphertext":"enc:v1:..."}`
   - `POST /api/secrets/decrypt` body `{"ciphertext":"enc:v1:..."}` → `{"plaintext":"..."}`
     (every decrypt emits an `Allow`/`Deny` audit-log entry).

   All four endpoints require an Admin-scoped API key and run behind
   the same `resolveApiKey` + `requireScope` chain as `/api/admin/*`.
   Object-store SSE-KMS and Postgres TDE remain infra-side concerns
   handled at deploy time and are not implemented in the edge.

5. **Compliance program.** SOC 2 Type II first (12-month runway), then
   HIPAA BAAs, GDPR DPA, ISO 27001. Annual pen-test.
6. **Self-observability.** Emit own metrics/logs/traces into a dedicated
   meta-tenant; dashboards + SLOs (`ingest_success_ratio > 99.9%`,
   `query_p99_latency < 1s`).

   ✅ **DONE (meta tenant + SLO recordings + curated dashboard).** A
   new `Self.fs` module owns the meta tenant. On startup in
   multi-tenant mode, `PulseBoard.Self.bootstrap` idempotently creates
   tenant slug `__meta__` via the configured `ITenantStore` and, if its
   dashboard repo is empty, upserts a curated `Dashboard` with id
   `pulse-self` titled *"PulseBoard — Self-Observability"*. Panels
   cover the existing internal counters and histograms — ingest
   throughput / errors, query volume and `pulse_query_p99_ms`, notify
   attempts / failures, rule eval seconds, quota denials — alongside
   the two new SLO recordings introduced here. `Self.startSloLoop`
   spawns a `Task.Run` loop (cadence = `max 5 intervalSec`, default
   30 s) that reads the last 5 minutes of `pulse_ingest_total` /
   `pulse_ingest_errors_total` and `pulse_notify_attempts_total` /
   `pulse_notify_failures_total` via `MetricStore.GetSince`, computes
   `success / (success + failure)`, and records:
   - `pulse_slo_ingest_success_ratio_5m`
   - `pulse_slo_notify_success_ratio_5m`

   Loop errors are swallowed so a transient store hiccup never poisons
   the self-observability pipe; the loop honors its
   `CancellationToken`. In single-tenant mode the meta tenant is
   skipped (no `__meta__` is created and no SLO loop runs).

---

## Phase 7 — Commercial surface (SaaS-only)

1. **Billing.** ✅ **DONE.** Stripe-shaped usage pipeline lives in
   [src/edge/Billing.fs](src/edge/Billing.fs). `IBillingMeter` exposes
   six commercial counters (`IngestBytes`, `LogBytes`, `ActiveSeries`,
   `TraceSpans`, `AlertEvals`, `Seats`) keyed by `(TenantId, UsageKind)`
   in a lock-free `ConcurrentDictionary` — `Record` is the hot path
   tapped by every receiver, `Snapshot` answers the admin endpoint,
   `Drain` atomically swaps counters to zero and emits one `UsageEvent`
   per non-empty cell. The pluggable `IBillingProvider` lets SaaS builds
   swap in a real Stripe adapter without touching the meter; OSS ships
   `FileBillingProvider` which serializes each rollup to
   `<dataDir>/billing/events.jsonl` (Stripe-compatible JSON shape:
   tenant, plan, kind, periodStart, periodEnd, quantity). A background
   `startRollupLoop` task drains on a 24h cadence (clamped ≥ 5s for
   safety) and ships every event to every registered provider. Cap
   guards: `CheckCap(plan, kind)` returns `Under | Soft | Hard` so the
   ingest path can short-circuit when a tenant blows past its plan
   ceiling (soft = warn + overage email, hard = 429). Ingest receivers
   (`metrics`, `logs`) now thread the meter through and record raw
   request bytes on success. Admin surface adds `GET
   /api/admin/tenants/<id>/usage` (live snapshot) and `POST
   /api/admin/billing/flush` (synchronous rollup for tests).
   Smoke-tested: signup → ingest → snapshot reports 30 bytes, flush
   writes one JSONL line, file provider tail roundtrips cleanly.

2. **Plans.** ✅ **DONE.**
   - **Free** — generous OSS-friendly limits, community support.
   - **Pro** — per-seat + usage; SSO via Google/Microsoft.
   - **Enterprise** — custom contract, SAML, audit export, BYOK, SLA.

   The plan catalog itself is owned by [src/edge/Plans.fs](src/edge/Plans.fs).
   Each tier carries `defaultRate : Kind -> Limit` (capacity + refillPerSec
   for the existing `Quotas` token bucket), `defaultCardinality : int`,
   and per-`UsageKind` soft caps; `toHardCap` scales soft by 1.5× and
   saturates at `MaxValue` so Enterprise stays unbounded. Feature gates
   ride on `Feature = Sso | Byok | Impersonation | CustomDomain` with
   `allows : Plan -> Feature -> bool` (Pro unlocks SSO; Enterprise
   unlocks BYOK, Impersonation, CustomDomain). The `Plan` discriminated
   union lives on the `Tenant` record itself (added to
   [src/edge/Tenancy.fs](src/edge/Tenancy.fs)) with `planToText` /
   `tryParsePlan` helpers and an `UpdateTenantPlan` member on
   `ITenantStore`. The in-memory store mutates the dict entry; the
   Postgres store ([src/edge/PgTenantStore.fs](src/edge/PgTenantStore.fs))
   gets an idempotent `ALTER TABLE pb_tenants ADD COLUMN IF NOT EXISTS
   plan TEXT NOT NULL DEFAULT 'free'` migration and an
   `UPDATE ... RETURNING tenant` member; every `SELECT` was updated to
   read the new column. Admin surface adds `PATCH
   /api/admin/tenants/<id>/plan` with `{"plan":"free|pro|enterprise"}`,
   and `tenantJson` now includes the plan in listings. Smoke-tested:
   tenant created with `plan=free`, PATCH lifts to `pro`, subsequent
   `GET /usage` reports `"plan":"pro"`. The plan-aware quota refresh on
   PATCH (re-binding `Quotas.QuotaStore` defaults to the new tier) is
   deliberately deferred — current tenants keep their bootstrap defaults
   plus any explicit overrides, which matches today's per-tenant
   override behavior; wiring `Plans.defaultRate` into `QuotaStore.Set`
   on plan change is a one-line follow-up.

3. **Onboarding.** ✅ **DONE.** Self-serve signup + snippet wizard live
   in [src/edge/Signup.fs](src/edge/Signup.fs), mounted before any
   tenant gate so it is reachable without a key. `POST /api/signup`
   accepts `{"slug","email"}`, validates the slug against `^[a-z][a-z0-9-]{2,31}$`,
   rejects a reserved list (`__meta__`, `admin`, `system`, `root`,
   `pulseboard`, `api`, `health`), checks for a slug collision (409),
   and on success creates the tenant + issues an Admin-scoped API key,
   returning one-shot plaintext + a deep-link `wizardUrl` carrying the
   key and tenant id. A per-IP `SignupRateLimiter` (5/hour by default,
   `ConcurrentDictionary` of `Bucket` records with lazy eviction)
   trusts `X-Forwarded-For` and falls back to Suave's
   `clientIpTrustProxy`; rate-limited callers get a 429 with the reset
   timestamp logged to the audit trail. `GET /api/wizard/snippets?lang=&host=&apiKey=`
   templates copy-paste blocks for nine languages (`node`, `python`,
   `go`, `java`, `otel`, `prom`, `docker`, `k8s`, `curl`), serialized
   via `Utf8JsonWriter` so the api key string escapes cleanly. Audit
   events are emitted for every signup attempt (allow, deny, error)
   with the remote IP attached so abuse patterns surface in
   `/api/admin/audit`. Smoke-tested: happy-path signup returns 201 +
   key + wizard URL; reserved/malformed/duplicate slugs return clean
   400/409s; wizard snippet retrieval roundtrips host and key into
   ready-to-paste templates.

4. **Marketplace integrations.** AWS CloudWatch, GCP Operations, Azure
   Monitor, GitHub Actions, Vercel, Render, Fly, Heroku — one-click installs.
5. **Docs site + API reference** (auto-generated from OpenAPI). Status
   page. Public roadmap.
6. **Support tooling.** Audited tenant impersonation, in-app chat
   (Intercom), incident comms.

---

## Phase 8 — Differentiation / moat

Pick 2-3 to over-invest in vs. competing flat.

1. **Cost transparency.** ✅ **DONE.** Per-team cost attribution +
   cardinality explorer live in [src/edge/Costs.fs](src/edge/Costs.fs).
   `ICostTracker` is tapped from the metrics ingest path: each accepted
   request fans out request bytes proportionally across the distinct
   `seriesName`s in the batch and records `(tenant, seriesName) ->
   samples + estimatedBytes` into a lock-free
   `ConcurrentDictionary<struct (TenantId * string), SeriesCell>`. Two
   Admin-scoped read endpoints expose the data: `GET
   /api/admin/tenants/<id>/cost/series?top=N` returns the top-N series
   ranked by sample count with bytes + projected monthly USD using the
   Pro IngestBytes overage rate, and `GET
   /api/admin/tenants/<id>/cost/teams` groups by a configurable
   `teamFor` policy (default: first dot-segment of the series name) so
   platform owners can attribute "team payments burned $X of the bill
   this month" without forcing label conventions on data producers.
   Smoke-tested with three series across two prefixes: `/cost/series`
   ranked them by sample count, `/cost/teams` aggregated `payments`
   (2 series, 6 samples) and `search` (1 series, 3 samples) correctly.
   Deliberately deferred: tap OTLP / Prom remote_write / scrape paths
   (currently only `/ingest/metrics` is wired), persist counters
   across rollups, and let tenants override the prefix policy through
   an Admin API.
2. **OSS self-hosted funnel.** Hardened PulseBoard as a polished, MIT/AGPL
   open-source edition. Lead-gen, not a sold product.
3. **Native AI assist.** ✅ **DONE.** `POST /api/ai/explain` (Query
   scope) lives in [src/edge/AiAssist.fs](src/edge/AiAssist.fs). The
   request body is `{ "seriesName", "samples":[{"ts","value"},...],
   "question"? }`; the response is `{ "provider", "summary",
   "annotations" }`. Provider is the pluggable `IAiProvider`; the OSS
   default is `EchoAiProvider`, a deterministic analyzer that computes
   mean, stddev, min, max, and the largest single-step jump in the
   window, classifies a "spike" as any jump exceeding 2× stddev, and
   stitches the result into a short prose summary plus structured
   annotations the UI can render. Echo runs entirely in-process — no
   model weights, no network — so the feature is *useful* even when no
   LLM provider is configured; the SaaS edge can swap in an
   OpenAI/Anthropic/local-vLLM adapter behind the same `IAiProvider`
   without touching this module. Smoke-tested on a 6-sample series
   with a 1.2→9.0 jump: Echo correctly identified the spike timestamp
   and reported `jump=7.90 > 2× stddev(2.95)`. Deliberately deferred:
   server-side context assembly (fetching samples from `MetricStore`
   given just a series name + range), exemplar trace / log correlation,
   and the per-tenant `ai.enabled` flag that gates whether data may
   leave the edge for an external model.
4. **Sub-second alerts.** Our WS hub already proves the live path —
   productize it for SLO burn-rate alerts; sell on time-to-detect where
   incumbents are minutes-late by design.
5. **Predictable pricing.** ✅ **DONE.** Public rate card + calculator
   live in [src/edge/Pricing.fs](src/edge/Pricing.fs) with a static
   companion UI at [src/edge/wwwroot/pricing.html](src/edge/wwwroot/pricing.html).
   The module owns one `PlanCard` per plan: monthly USD base, seats
   included, and per-`UsageKind` `OverageRate` (cents-per-unit + unit
   name + raw→billable conversion). Two unauthenticated endpoints back
   it: `GET /api/pricing` emits the full rate card with included soft
   caps (Free: 5 GiB/1 GiB/10k series/1M spans/10M evals/3 seats; Pro:
   $99/mo base + 5 seats, 250 GiB/100 GiB/250k/50M/500M/25 included
   then $0.50/GiB ingest, $0.30/GiB log, $0.08/1k series, $0.10/1M
   spans, $0.02/1M evals, $15/seat; Enterprise: contract-bound); `POST
   /api/pricing/estimate` accepts an opaque `{ingestBytes,logBytes,...}`
   object and returns the line-itemed Estimate for every plan. The
   HTML calculator at `/pricing` calls the same endpoint, so the
   billing math customers preview is *literally the same math* the
   invoice runs through — that's the bill-shock promise. Smoke-tested
   with 300 GiB ingest + 50 GiB logs + 100M spans + 8 seats: Free shows
   $0 (caps blow past, calculator surfaces over-cap counts), Pro
   correctly computes $99 base + $14.70 ingest overage + $5.00 trace
   overage = $118.70/mo, Enterprise shows $0 (contract path).
   Deliberately deferred: GIb→GB unit toggle on the UI, a "what plan
   should I pick?" recommendation column, and signed/cacheable
   `/api/pricing` so the public calculator can be served from a CDN.

6. **Public product website.** ✅ **DONE.** The edge now ships a
   complete public marketing site served from the same binary, with no
   external dependencies. Four new static pages live under
   [src/edge/wwwroot/](src/edge/wwwroot/): `home.html` (landing — hero,
   three moat-aligned feature cards, 30-second ingest snippet, three-
   plan pricing teaser, footer with full nav), `docs.html` (single-page
   documentation with sticky table of contents covering Quickstart,
   Auth, Ingest, OTLP/Prom/Loki, Query, Alerts, AI assist, Cost
   transparency, Pricing, Admin/RBAC, Self-hosting, License),
   `signup.html` (POSTs to `/api/signup`, then displays the one-time
   API key + stashes it in `sessionStorage` under `pb.bearer` so the
   dashboard picks it up automatically), and `signin.html` (paste-key
   form that probes `/auth/me` to validate before redirecting to
   `/admin`). The existing `pricing.html` was retrofitted with the
   shared top-nav so all four pages share the same visual language.
   Routes were rearranged in [src/edge/Program.fs](src/edge/Program.fs):
   `/` now serves the marketing landing (previously the dashboard) and
   the dashboard SPA moved to `/app` (with `/dashboard` and `/app.html`
   aliases). The `wizardUrl` returned by `/api/signup`
   (`/onboard?key=...&tenant=...`) now resolves to a real file
   (signup.html serving as the onboarding success view). Smoke-tested
   with all eight routes (`/`, `/docs`, `/signup`, `/signin`,
   `/pricing`, `/app`, `/admin`, `/onboard`) returning HTTP 200.
   Deliberately deferred: separate `examples.html` page (currently
   inlined into the landing + docs); a `/changelog` page wired to git
   tags; per-page Open Graph metadata for link previews; and a CDN
   asset pipeline for `home.html`/`docs.html` so they can be served
   independently of the edge.

---

## Phase 9 — Hosted product / provisioner

User feedback after Phase 8 #6 surfaced a real conceptual gap: the
same binary today is trying to be both the *public marketing site* at
`pulseboard.cloud` and a *per-customer workspace* at
`<slug>.pulseboard.cloud`. Functionally fine for self-hosters; visibly
wrong for a hosted product (the public nav linking to `/admin` and
`/app` makes no sense to a prospect, and `/api/signup` only works in
multi-tenant mode which a single-tenant binary doesn't enable).

The hosted shape we want:

```
visitor → pulseboard.cloud (marketing only, no /app or /admin)
           │ POST /api/signup
           ▼
        provisioner ──► allocates slug, spawns Fly Machine, bootstraps key
           │
           ▼
        https://acme-7f3a.pulseboard.cloud (workspace: /app + /admin + /ingest)
```

Picked stack: **Fly Machines** (one app per customer, `pb-<slug>`) +
**Caddy on-demand TLS** in front (`*.pulseboard.cloud` with an
`ask` endpoint to the provisioner). See
[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) §6 for the full picture.

Three pieces of work — first cut shipped:

1. **`--site-only` mode** — DONE. `src/edge/SiteOnly.fs`. Serves only
   marketing routes; `POST /api/signup` is proxied to
   `--provisioner-url=…`. No tenant store, no quotas, no ingest /
   query / admin routes.

2. **Provisioner service** — DONE (first cut). `src/edge/Provisioner.fs`.
   Run with `--mode=provisioner`. Endpoints:
   - `POST /api/provision` — allocates slug, calls Fly Machines API,
     bootstraps first key on the new workspace, returns
     `{slug, url, apiKey, tenantId}`.
   - `GET /provision/ask?domain=<host>` — 200/404 for Caddy on-demand TLS.
   - `GET /provision/route?domain=<host>` — upstream lookup for Caddy
     dynamic reverse_proxy.

   `IFlyClient` has both a real `HttpFlyClient` (api.machines.dev/v1)
   and a `DryRunFlyClient` that logs intent and returns synthetic
   IDs, so the end-to-end flow is testable locally without Fly
   credentials.

3. **Workspace bootstrap** — first cut works (provisioner POSTs to the
   new machine's own `/api/signup` to mint the first key). Hardening
   TODO before production:
   - Single-use bootstrap secret passed via Fly env, gating
     `/api/signup` until the first key is issued.
   - `bootstrapped=true` flag flipped after first call so subsequent
     `/api/signup` requires operator action.

Still to do:

- **`infra/Caddyfile`** — DONE. Wildcard TLS with `ask` + dynamic
  upstreams.
- Postgres-backed `IWorkspaceRegistry` (in-memory today; restarting
  the provisioner forgets allocations).
- Workspace teardown (cancel-on-failed-payment, evict-on-inactive,
  scale-to-zero behaviour).
- Real Stripe linkage on signup (plan selection, payment method).
- Multi-region: today every workspace lands in one Fly region
  (`--fly-region=`); customer-chosen region needs marketing-side UI.
- Hardening of the bootstrap call (item 3 above).

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

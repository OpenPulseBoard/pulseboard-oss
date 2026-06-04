# HTTP API reference

A directory of every HTTP endpoint in the OSS edge runtime. Auth requirements
apply in **multi-tenant** mode; in single-tenant mode reads/writes use HTTP Basic
(or are open if no tokens are configured). All routes were taken directly from
the route tables in [src/edge/](../../src/edge/).

Authenticate with a bearer token: `Authorization: Bearer pk_<id>.<secret>`.

## Ingest — scope `Ingest`

| Method | Path | Purpose |
|--------|------|---------|
| POST | `/ingest/metrics` | Native JSON/NDJSON metrics. |
| POST | `/ingest/logs` | Native JSON/NDJSON logs. |
| POST | `/api/v1/write` | Prometheus remote-write. |
| POST | `/api/prom/push` | Mimir/Cortex alias for remote-write. |
| POST | `/v1/metrics` | OTLP metrics. |
| POST | `/v1/logs` | OTLP logs. |
| POST | `/v1/traces` | OTLP traces. |
| POST | `/loki/api/v1/push` | Loki push. |
| POST | `/rum/v1/events` | RUM beacons (single-tenant). |
| POST | `/rum/v1/{tenantId}/events` | RUM beacons (multi-tenant; unauthenticated stub). |

## Query — scope `Query`

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/api/metrics` | List series names. |
| GET | `/api/metrics/{name}` | Series data (`sinceMs`, `step`, `agg`). |
| GET | `/api/logs` | Log tail (`tail=N`). |
| GET | `/api/prom/api/v1/query` | PromQL instant query. |
| GET | `/api/prom/api/v1/query_range` | PromQL range query. |
| GET | `/api/prom/api/v1/labels` | Label names. |
| GET | `/api/prom/api/v1/label/{name}/values` | Label values. |
| GET | `/api/prom/api/v1/series` | Series matching matchers. |
| GET | `/api/loki/api/v1/query_range` | LogQL range query. |
| GET | `/api/loki/api/v1/labels` | Log label names. |
| GET | `/api/loki/api/v1/label/{name}/values` | Log label values. |
| GET | `/api/traces` | Recent trace summaries (`sinceMs`, `limit`). |
| GET | `/api/traces/{traceId}` | Full trace. |
| GET | `/api/servicemap` | Service graph. |

## Dashboards — scope `Query`

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/api/dashboards` | List. |
| POST | `/api/dashboards` | Create. |
| GET | `/api/dashboards/{id}` | Read. |
| PUT | `/api/dashboards/{id}` | Update. |
| DELETE | `/api/dashboards/{id}` | Delete. |

## Alerting — rules

| Method | Path | Scope | Purpose |
|--------|------|-------|---------|
| GET | `/api/rules` | Query | List rule groups. |
| GET | `/api/rules/{id}` | Query | Read one group. |
| POST | `/api/rules` | Admin | Create a group (server assigns id). |
| PUT | `/api/rules/{id}` | Admin | Upsert a group at id. |
| DELETE | `/api/rules/{id}` | Admin | Delete a group. |
| GET | `/api/alerts` | Query | Active alert instances. |

## Alerting — routing and silences

| Method | Path | Scope | Purpose |
|--------|------|-------|---------|
| GET | `/api/alertmanager/config` | Query | Read routing + receivers + inhibitions + mute times. |
| PUT | `/api/alertmanager/config` | Admin | Replace the full config. |
| GET | `/api/silences` | Query | List silences. |
| POST | `/api/silences` | Admin | Create/upsert a silence. |
| DELETE | `/api/silences/{id}` | Admin | Remove a silence. |

## Alerting — on-call and acks

| Method | Path | Scope | Purpose |
|--------|------|-------|---------|
| GET | `/api/oncall/catalog` | Query | Read users/schedules/policies. |
| PUT | `/api/oncall/catalog` | Admin | Replace the catalog. |
| GET | `/api/oncall/whoison/{scheduleId}` | Query | Current on-call for a schedule. |
| POST | `/api/alerts/{fingerprint}/ack` | Query | Acknowledge an alert. |
| GET | `/api/alerts/{fingerprint}/acks` | Query | List acks for an alert. |

## Alerting — runbooks

| Method | Path | Scope | Purpose |
|--------|------|-------|---------|
| GET | `/api/alerts/{fingerprint}/runbook` | Query | Runbook + progress for an alert. |
| POST | `/api/alerts/{fingerprint}/runbook/step` | Query | Mark a step complete/undone. |
| GET | `/api/runbooks/incidents` | Query | Post-incident MTTR analytics. |

## Notifications — scope `Admin`

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/api/notify/queue` | Pending outbound messages. |
| GET | `/api/notify/dlq` | Dead-lettered messages. |
| POST | `/api/notify/dlq/{id}/replay` | Re-queue a dead letter. |
| DELETE | `/api/notify/dlq/{id}` | Purge a dead letter. |

## Admin (multi-tenant only) — scope `Admin`

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/api/admin/audit` | Recent audit events (`tail=N`). |
| GET | `/api/admin/tenants` | List tenants. |
| POST | `/api/admin/tenants` | Create a tenant. |
| PATCH | `/api/admin/tenants/{id}/plan` | Change plan. |
| GET | `/api/admin/tenants/{id}/api-keys` | List API keys. |
| POST | `/api/admin/tenants/{id}/api-keys` | Issue an API key. |
| GET | `/api/admin/tenants/{id}/users` | List SSO users. |
| PATCH | `/api/admin/users/{id}` | Change a user's role. |
| GET | `/api/admin/tenants/{id}/quotas` | Read quota overrides. |
| PUT | `/api/admin/tenants/{id}/quotas` | Set quota overrides. |
| GET | `/api/admin/tenants/{id}/cardinality` | Active-series cardinality. |
| GET | `/api/admin/tenants/{id}/retention` | Read retention overrides. |
| PUT | `/api/admin/tenants/{id}/retention` | Set retention overrides. |
| GET | `/api/admin/tenants/{id}/scrape-targets` | List scrape targets. |
| POST | `/api/admin/tenants/{id}/scrape-targets` | Add a scrape target. |
| GET | `/api/admin/scrape-targets/{id}` | Scrape-target status. |
| DELETE | `/api/admin/scrape-targets/{id}` | Remove a scrape target. |
| GET | `/api/admin/tenants/{id}/listeners` | List StatsD/Carbon listeners. |
| POST | `/api/admin/tenants/{id}/listeners` | Add a listener. |
| GET | `/api/admin/listeners/{id}` | Listener status. |
| DELETE | `/api/admin/listeners/{id}` | Remove a listener. |
| GET | `/api/admin/tenants/{id}/usage` | Current-period usage. |
| POST | `/api/admin/billing/flush` | Force a usage rollup. |
| GET | `/api/admin/tenants/{id}/cost/series` | Top series by estimated cost. |
| GET | `/api/admin/tenants/{id}/cost/teams` | Cost aggregated by team. |

## Secrets — scope `Admin`

| Method | Path | Purpose |
|--------|------|---------|
| POST | `/api/secrets/encrypt` | Encrypt a value. |
| POST | `/api/secrets/decrypt` | Decrypt a value. |
| GET | `/api/secrets/policy` | Read PII auto-encrypt patterns. |
| PUT | `/api/secrets/policy` | Set PII patterns. |

## Pricing and AI

| Method | Path | Scope | Purpose |
|--------|------|-------|---------|
| GET | `/api/pricing` | public | Public rate card. |
| POST | `/api/pricing/estimate` | public | Cost estimate from a usage profile. |
| POST | `/api/ai/explain` | Query | Natural-language explanation of a query/alert. |

## Agents

| Method | Path | Scope | Purpose |
|--------|------|-------|---------|
| POST | `/api/agent/v1/enroll` | enrollment token | Enroll an agent. |
| POST | `/api/agent/v1/checkin` | agent token | Agent heartbeat/check-in. |
| GET | `/api/agent/v1/config` | agent token | Fetch agent config. |
| GET | `/api/agents` | Admin | List enrolled agents. |
| POST | `/api/agents/token` | Admin | Mint an enrollment token. |

## Auth and sign-up

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/auth/login` | Start OIDC login (when OIDC configured). |
| GET | `/auth/callback` | OIDC callback; mints a session cookie. |
| GET | `/auth/logout` | Clear the session. |
| GET | `/auth/me` | Current principal. |
| POST | `/api/signup` | Self-serve tenant sign-up (multi-tenant). |
| GET | `/api/wizard/snippets` | Onboarding code snippets. |
| POST | `/api/bootstrap/keys` | Issue a key with `--bootstrap-token=`. |

## Realtime and static

| Method | Path | Purpose |
|--------|------|---------|
| WS | `/ws` | Live alert/dashboard feed. |
| GET | `/healthz`, `/api/healthz` | Liveness probe. |
| GET | `/` | `workspace.html` (multi-tenant) or `home.html` (single-tenant). |
| GET | `/app` | Dashboard SPA (`index.html`). |
| GET | `/admin` | Admin UI (multi-tenant). |
| GET | `/pricing` | Rate-card calculator (multi-tenant). |
| GET | `/signin` | API-key sign-in form. |
| GET | `/docs` | Bundled docs page. |
| GET | `/live` | Live view. |

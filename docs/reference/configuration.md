# Configuration reference

Every option can be set as a CLI flag **or** an environment variable. When both
are present, the **CLI flag wins**. This reference covers the OSS edge runtime.

## Startup

| Flag | Env | Default | Purpose |
|------|-----|---------|---------|
| `--port=N` | — | 8080 | HTTP listen port. |
| `--data=PATH` | `PULSE_DATA_DIR` | `./pulse-data` | Root data directory. |
| — | `PULSE_BIND_ADDR` | `127.0.0.1` | Listen address(es); comma-separated for dual-stack (e.g. `::,0.0.0.0`). |
| `--role=all\|edge\|storage` | `PULSE_ROLE` | `all` | Deployment topology. |
| `--storage-endpoint=URL` | `PULSE_STORAGE_ENDPOINT` | — | Storage-tier URL (required for `--role=edge`). |
| `--edge-secret=HEX` | `PULSE_EDGE_SECRET` | — | HMAC key for the internal edge↔storage protocol (required for `--role=edge\|storage`). |

## Multi-tenancy and auth

| Flag | Env | Default | Purpose |
|------|-----|---------|---------|
| `--multi-tenant` | — | off | Enable multi-tenant mode (API keys + RBAC). |
| `--seed-tenant=SLUG` | — | — | Auto-create a seed tenant on startup. |
| `--postgres=CONNSTR` | `PULSE_POSTGRES` | — | Postgres connection string (enables persistent stores). |
| `--tokens-file=PATH` | `PULSE_TOKENS` | — | HTTP Basic tokens for single-tenant mode (`name:secret` per line). |

## OIDC SSO

| Flag | Env | Default |
|------|-----|---------|
| `--oidc-issuer=URL` | `PULSE_OIDC_ISSUER` | — |
| `--oidc-client-id=STR` | `PULSE_OIDC_CLIENT_ID` | — |
| `--oidc-client-secret=STR` | `PULSE_OIDC_CLIENT_SECRET` | — (PKCE if omitted) |
| `--oidc-redirect-uri=URL` | `PULSE_OIDC_REDIRECT_URI` | — |
| `--oidc-tenant=SLUG` | `PULSE_OIDC_TENANT` | — |
| `--oidc-scopes=STR` | `PULSE_OIDC_SCOPES` | `openid email profile` |
| `--oidc-default-role=ROLE` | `PULSE_OIDC_DEFAULT_ROLE` | deny |
| `--oidc-admins=EMAILS` | `PULSE_OIDC_ADMINS` | — |
| `--oidc-editors=EMAILS` | `PULSE_OIDC_EDITORS` | — |
| `--oidc-viewers=EMAILS` | `PULSE_OIDC_VIEWERS` | — |
| `--oidc-billing=EMAILS` | `PULSE_OIDC_BILLING` | — |
| `--session-secret=BASE64` | `PULSE_SESSION_SECRET` | auto-generated |

## Quotas and rate limiting

| Flag | Env | Default |
|------|-----|---------|
| `--quota-ingest-rps=F` | `PULSE_QUOTA_INGEST_RPS` | 500.0 |
| `--quota-ingest-burst=F` | `PULSE_QUOTA_INGEST_BURST` | 1000.0 |
| `--quota-query-rps=F` | `PULSE_QUOTA_QUERY_RPS` | 100.0 |
| `--quota-query-burst=F` | `PULSE_QUOTA_QUERY_BURST` | 200.0 |
| `--quota-alert-eval-rps=F` | `PULSE_QUOTA_ALERT_EVAL_RPS` | 0.0 (off) |
| `--quota-alert-eval-burst=F` | `PULSE_QUOTA_ALERT_EVAL_BURST` | 0.0 |
| `--quota-log-bytes-per-sec=F` | `PULSE_QUOTA_LOG_BPS` | 0.0 (off) |
| `--quota-log-burst-bytes=F` | `PULSE_QUOTA_LOG_BURST_BYTES` | 0.0 |
| `--quota-cardinality=N` | `PULSE_QUOTA_CARDINALITY` | 0 (unlimited) |

## External backends

| Flag | Env | Default |
|------|-----|---------|
| `--mimir-url=URL` | `PULSE_MIMIR_URL` | — |
| `--mimir-org-header=NAME` | `PULSE_MIMIR_ORG_HEADER` | `X-Scope-OrgID` |
| `--mimir-bearer=TOKEN` | `PULSE_MIMIR_BEARER` | — |
| `--mimir-read-tenant=ID` | `PULSE_MIMIR_READ_TENANT` | — |
| `--mimir-step-ms=N` | `PULSE_MIMIR_STEP_MS` | 15000 |
| `--loki-url=URL` | `PULSE_LOKI_URL` | — |
| `--loki-org-header=NAME` | `PULSE_LOKI_ORG_HEADER` | `X-Scope-OrgID` |
| `--loki-bearer=TOKEN` | `PULSE_LOKI_BEARER` | — |
| `--tempo-url=URL` | `PULSE_TEMPO_URL` | — |
| `--tempo-org-header=NAME` | `PULSE_TEMPO_ORG_HEADER` | `X-Scope-OrgID` |
| `--tempo-bearer=TOKEN` | `PULSE_TEMPO_BEARER` | — |
| `--metrics-s3-bucket=BUCKET` | `PULSE_METRICS_S3_BUCKET` | — |
| `--metrics-s3-prefix=PREFIX` | `PULSE_METRICS_S3_PREFIX` | `metrics/` |
| `--metrics-s3-region=REGION` | `PULSE_METRICS_S3_REGION` | — |
| `--metrics-s3-endpoint=URL` | `PULSE_METRICS_S3_ENDPOINT` | — |

## Retention and rollups

| Flag | Env | Default |
|------|-----|---------|
| `--retention-metrics-ms=MS` | `PULSE_RETENTION_METRICS_MS` | keep forever |
| `--retention-logs-ms=MS` | `PULSE_RETENTION_LOGS_MS` | keep forever |
| `--retention-traces-ms=MS` | `PULSE_RETENTION_TRACES_MS` | keep forever |
| `--retention-compact-interval-ms=MS` | `PULSE_RETENTION_COMPACT_INTERVAL_MS` | 60000 |
| `--rollups-enabled=BOOL` | `PULSE_ROLLUPS_ENABLED` | true |
| `--rollups-interval-ms=MS` | `PULSE_ROLLUPS_INTERVAL_MS` | 30000 |

## Alert delivery

| Flag | Env | Default | Purpose |
|------|-----|---------|---------|
| `--webhook=URL` | `PULSE_WEBHOOKS` | — | Generic webhook (repeatable; env is comma-separated). |
| `--slack=URL` | `PULSE_SLACK` | — | Slack webhook (repeatable; env is comma-separated). |
| `--public-url=URL` | `PULSE_PUBLIC_URL` | relative links | Public base URL used for runbook deep links in notifications. |

## Secrets

| Flag | Env | Default | Purpose |
|------|-----|---------|---------|
| — | `PULSE_MASTER_KEY` | auto-generated at `<dataDir>/secrets/master.key` | Master KEK (base64, 32 bytes) for envelope encryption. |

## Audit export

| Flag | Env | Default | Purpose |
|------|-----|---------|---------|
| `--audit-s3-bucket=BUCKET` | `PULSE_AUDIT_S3_BUCKET` | — | Nightly audit log export (requires `--postgres=`). |
| `--audit-s3-prefix=PREFIX` | `PULSE_AUDIT_S3_PREFIX` | — | S3 key prefix. |
| `--audit-s3-region=REGION` | `PULSE_AUDIT_S3_REGION` | — | AWS region. |
| `--audit-s3-endpoint=URL` | `PULSE_AUDIT_S3_ENDPOINT` | — | S3-compatible endpoint. |

## Provisioning

| Flag | Env | Default | Purpose |
|------|-----|---------|---------|
| `--bootstrap-token=TOKEN` | `PULSE_BOOTSTRAP_TOKEN` | — | Bearer token that mounts `POST /api/bootstrap/keys` for automated key issuance. |

## Examples

Single-tenant, open ingest (local dev):

```bash
pulseboard --port=8080 --data=./pulse-data
```

Single-tenant with Basic auth:

```bash
pulseboard --tokens-file=./tokens.txt --slack=https://hooks.slack.com/services/…
```

Multi-tenant with Postgres and external stores:

```bash
pulseboard --multi-tenant --seed-tenant=acme \
  --postgres="Host=pg;Database=pulse;Username=pulse;Password=…" \
  --mimir-url=http://mimir:9009 --loki-url=http://loki:3100 \
  --public-url=https://obs.example.com
```

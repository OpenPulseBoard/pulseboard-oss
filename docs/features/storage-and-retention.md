# Storage, retention and rollups

PulseBoard is a single binary with **embedded** stores by default, and can
optionally offload each signal to an external system. Storage selection is
driven by flags; nothing is required to get started.

Source: [Storage.fs](../../src/edge/Storage.fs), [Segments.fs](../../src/edge/Segments.fs),
[Retention.fs](../../src/edge/Retention.fs), [CloudBackends.fs](../../src/edge/CloudBackends.fs),
[Program.fs](../../src/edge/Program.fs).

## Backend selection at a glance

| Signal | Embedded (default) | External option | Flag |
|--------|--------------------|-----------------|------|
| Metrics | in-memory ring + on-disk segments | Mimir (remote-write + query proxy) | `--mimir-url=` |
| Metrics (cold) | local segment files | S3 / MinIO / Ceph segments | `--metrics-s3-bucket=` |
| Logs | in-memory ring | Loki (push + query proxy) | `--loki-url=` |
| Traces | in-memory ring (10k/tenant) | Tempo (OTLP push) | `--tempo-url=` |
| Control-plane state | files under `<dataDir>` | Postgres | `--postgres=` |

## Metric storage

- **Embedded:** an in-memory ring per metric, persisted to disk as `SegWire`
  segment files under `<dataDir>/.segments/<metric>/<resolution>/<time>.seg`.
  TTL-based compaction prunes old data.
- **S3 segments:** the same segment format written to object storage
  (`s3://<bucket>/<prefix>/<tenant>/<metric>/<resolution>/<bucket>.seg`). Works
  with AWS S3, MinIO, Ceph and SeaweedFS via the AWS default credential chain.
- **Mimir:** writes go out as remote-write (`POST <mimir>/api/v1/push`); reads
  proxy PromQL to Mimir's HTTP API. Tenant is sent via `X-Scope-OrgID` (override
  with `--mimir-org-header=`).

## Log storage

- **Embedded:** in-memory ring with TTL enforcement.
- **Loki:** push to `POST <loki>/loki/api/v1/push` (snappy-protobuf); LogQL
  reads proxy to Loki. Tenant via `X-Scope-OrgID` (or `--loki-org-header=`).

## Trace storage

- **Embedded:** in-memory ring per tenant (default 10k spans), powering the
  Traces and Service Map views. **Not persisted — lost on restart.**
- **Tempo:** spans are forwarded to `POST <tempo>/v1/traces`. Reads still come
  from the in-process ring.

## Retention

Set TTLs to bound how long each signal is kept (unset = keep forever):

| Flag | Env |
|------|-----|
| `--retention-metrics-ms=` | `PULSE_RETENTION_METRICS_MS` |
| `--retention-logs-ms=` | `PULSE_RETENTION_LOGS_MS` |
| `--retention-traces-ms=` | `PULSE_RETENTION_TRACES_MS` |
| `--retention-compact-interval-ms=` | `PULSE_RETENTION_COMPACT_INTERVAL_MS` (default 60000) |

The embedded compactor runs on the compact interval and drops data older than
the TTL. Per-tenant retention overrides are available via
`PUT /api/admin/tenants/{id}/retention`.

## Downsampling rollups

To keep long-range queries cheap, the embedded store maintains 1m / 5m / 1h
rollups:

| Flag | Env | Default |
|------|-----|---------|
| `--rollups-enabled=` | `PULSE_ROLLUPS_ENABLED` | true |
| `--rollups-interval-ms=` | `PULSE_ROLLUPS_INTERVAL_MS` | 30000 |

`GET /api/metrics/{name}?step=auto` automatically picks raw / 1m / 5m / 1h based
on the requested window (see [querying-and-dashboards.md](querying-and-dashboards.md)).

## Postgres-backed control plane

When `--postgres=` is set, control-plane stores persist to Postgres instead of
files. Schemas are created automatically at startup. Tables:

| Table | Holds |
|-------|-------|
| `pb_tenants`, `pb_api_keys`, `pb_users` | Tenants, keys, SSO users |
| `pb_audit_events` | Audit trail |
| `pb_rules` | Rule groups |
| `pb_routing_config` | Routing / receivers / silences |
| `pb_outbound_messages` | Notification queue + DLQ |
| `pb_oncall_catalog`, `pb_acks` | On-call catalog + acks |
| `pb_runbook_progress` | Runbook completion tracking |
| `pb_dashboards` | Dashboards |
| `pb_quota_overrides`, `pb_retention_overrides` | Per-tenant overrides |
| `pb_deks`, `pb_pii_policy` | Encryption keys + PII patterns |
| `pb_agents` | Agent enrollment |
| `pb_billing_events` | Usage events |

## File layout (OSS default, no Postgres)

Everything lives under `<dataDir>` (`--data=` / `PULSE_DATA_DIR`, default
`./pulse-data`):

| Path | Content |
|------|---------|
| `rules/<tenant>/<group>.json` | Rule groups |
| `routing/<tenant>.json` | Routing config |
| `notify/queue.ndjson`, `notify/dlq.ndjson` | Notifications |
| `oncall/<tenant>.json`, `acks/<tenant>.ndjson` | On-call + acks |
| `runbooks/<tenant>.ndjson` | Runbook progress |
| `dashboards/<tenant>/<id>.json` | Dashboards |
| `secrets/master.key`, `secrets/<tenant>.dek` | Encryption keys |
| `billing/events.jsonl` | Usage events |
| `.segments/<metric>/<resolution>/<time>.seg` | Metric segments |

## Deployment roles

`--role=` selects the topology: `all` (monolith, default), `edge` (stateless
front that forwards to a storage tier at `--storage-endpoint=`), or `storage`
(the backing tier). The internal edge↔storage protocol is HMAC-authenticated
with `--edge-secret=`.

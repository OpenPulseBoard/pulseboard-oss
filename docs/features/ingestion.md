# Ingestion

PulseBoard accepts metrics, logs and traces over several wire protocols so it
can drop into existing Prometheus / OpenTelemetry / Loki setups. All push
endpoints require the **Ingest** scope in multi-tenant mode; in single-tenant
mode they use HTTP Basic auth (or are open if no tokens are configured — with a
loud startup warning).

Source: [Ingest.fs](../../src/edge/Ingest.fs),
[PromRemoteWrite.fs](../../src/edge/PromRemoteWrite.fs),
[Otlp.fs](../../src/edge/Otlp.fs), [LokiPush.fs](../../src/edge/LokiPush.fs),
[Rum.fs](../../src/edge/Rum.fs).

## Protocol summary

| Protocol | Endpoint(s) | Body | Signal |
|----------|-------------|------|--------|
| Native JSON / NDJSON | `POST /ingest/metrics`, `POST /ingest/logs` | JSON object, array, or NDJSON | metrics, logs |
| Prometheus remote-write 1.0 | `POST /api/v1/write`, `POST /api/prom/push` | snappy-compressed protobuf `WriteRequest` | metrics |
| OTLP/HTTP | `POST /v1/metrics`, `POST /v1/logs`, `POST /v1/traces` | protobuf export request | metrics, logs, traces |
| Loki push | `POST /loki/api/v1/push` | JSON or snappy-protobuf `PushRequest` | logs |
| Prometheus scrape (pull) | configured per tenant | text exposition format | metrics |
| RUM beacons | `POST /rum/v1/events`, `POST /rum/v1/{tenantId}/events` | JSON array | metrics, logs |

## Native JSON / NDJSON

The simplest path — no protobuf required.

```bash
# metrics
curl -X POST http://localhost:8080/ingest/metrics \
  -H "Content-Type: application/json" \
  -d '[{"name":"cpu","value":0.42,"ts":1705329000000},
       {"name":"cpu","value":0.55}]'

# logs
curl -X POST http://localhost:8080/ingest/logs \
  -H "Content-Type: application/json" \
  -d '[{"service":"api","level":"error","message":"boom"}]'
```

- `ts` (unix ms) is optional for metrics and defaults to `now()`.
- Responses report `{"accepted": N, "rejected": N, ...}`.
- Metric ingest is rate-limited by `PULSE_QUOTA_INGEST_RPS` / `..._BURST`.
- Log ingest is byte-rate-limited by `PULSE_QUOTA_LOG_BPS` / `..._BURST_BYTES`.

## Prometheus remote-write

Point any Prometheus or Grafana Agent `remote_write` block at PulseBoard:

```yaml
remote_write:
  - url: http://localhost:8080/api/v1/write
    authorization:
      credentials: pk_<id>.<secret>   # multi-tenant
```

- `/api/prom/push` is a Cortex/Mimir-convention alias for the same handler.
- Body is snappy-compressed protobuf (`prompb` `WriteRequest`).
- Prometheus "stale marker" NaN samples are silently dropped.
- Returns `204 No Content` on success.

## OTLP/HTTP (OpenTelemetry)

Send OTLP over HTTP/protobuf to the standard paths:

| Endpoint | Accepts |
|----------|---------|
| `POST /v1/metrics` | `ExportMetricsServiceRequest` — Gauge and Sum `NumberDataPoint`s. Histogram / ExponentialHistogram / Summary are skipped. |
| `POST /v1/logs` | `ExportLogsServiceRequest` — time, severity, body and attributes are extracted. |
| `POST /v1/traces` | `ExportTraceServiceRequest` — spans land in an in-memory ring (and optional Tempo passthrough). |

OTLP timestamps are nanoseconds and are converted to milliseconds internally.

## Loki push

```bash
curl -X POST http://localhost:8080/loki/api/v1/push \
  -H "Content-Type: application/json" \
  -d '{"streams":[{"stream":{"service_name":"api","level":"error"},
                   "values":[["1705329000000000000","boom"]]}]}'
```

- Accepts JSON or snappy-protobuf `logproto.PushRequest`.
- `service` and `level` are read from the `service_name` / `level` labels.
- Returns `204 No Content`.

## Prometheus scrape (pull)

In multi-tenant mode you can register scrape targets and PulseBoard's background
worker pulls them on an interval:

```bash
curl -X POST http://localhost:8080/api/admin/tenants/<id>/scrape-targets \
  -H "Authorization: Bearer pk_<admin-key>" \
  -H "Content-Type: application/json" \
  -d '{"url":"http://localhost:9090/metrics","intervalSec":30,
       "labels":{"job":"prometheus"},"bearerToken":null}'
```

Target status (last scrape time, sample count, duration, error) is available at
`GET /api/admin/scrape-targets/{id}`. Remove a target with `DELETE`.

## RUM (Real User Monitoring)

Browser beacons land at `POST /rum/v1/events` (single-tenant) or
`POST /rum/v1/{tenantId}/events` (multi-tenant). The body is a JSON array of
beacons:

- `web_vital` → metric `rum_<name>_<unit>`
- `page_load` → metric `rum_page_load_ms`
- `error` → a log entry at `level=error`, `service=rum`

This endpoint is an **unauthenticated stub**: in multi-tenant mode the tenant id
comes from the URL path and is not validated against published keys. Oversized
payloads return `413`.

## Series naming

Metrics are stored in canonical Prometheus form:

```
<metric_name>{<label1>="<value1>",<label2>="<value2>"}
```

Labels are sorted by name, values are double-quote-wrapped, and backslash /
double-quote / newline are escaped. Cardinality can be capped per tenant with
`PULSE_QUOTA_CARDINALITY`.

See [storage-and-retention.md](storage-and-retention.md) for where ingested data
lands and how long it is kept.

# PulseBoard

> Real-time metrics, logs, and alerts — a fast, opinionated observability platform.

PulseBoard started as a single-binary F#/Suave demo and is on its way to a
full SaaS observability product. The roadmap, decisions, and acceptance
tests are in [PLAN.md](PLAN.md).

| | |
| --- | --- |
| **Status** | Pre-alpha. APIs and storage formats can change without notice. |
| **License** | [AGPL-3.0-or-later](LICENSE). The OSS edition is a lead-gen funnel for the (forthcoming) PulseBoard Cloud SaaS; commercial licenses available on request. |
| **Language** | F# on .NET 10 |
| **HTTP stack** | [Suave](https://suave.io) |

---

## Quick start

```bash
# Build
dotnet build

# Run on :8775 with on-disk metric history and open ingest
dotnet run --project src/edge -- --port=8775 --data=./pulse-data

# Or with HTTP Basic on /ingest/* + Slack/webhook delivery
printf 'agent1:s3cret\n' > tokens.txt
dotnet run --project src/edge -- \
  --port=8775 --data=./pulse-data \
  --tokens-file=tokens.txt \
  --slack=https://hooks.slack.com/services/T000/B000/XXXX \
  --webhook=https://example.com/alerts

# Send a metric
curl -u agent1:s3cret -X POST -H 'content-type: application/json' \
  -d '{"name":"cpu","value":0.5}' http://127.0.0.1:8775/ingest/metrics

# Dashboard
open http://127.0.0.1:8775/
```



---

## What's in the box today

| Capability         | Where                                            |
| ------------------ | ------------------------------------------------ |
| In-memory ring + on-disk segment store for metrics | [src/edge/Segments.fs](src/edge/Segments.fs), [src/edge/TimeSeries.fs](src/edge/TimeSeries.fs) |
| JSON / NDJSON ingest (`/ingest/metrics`, `/ingest/logs`) | [src/edge/Ingest.fs](src/edge/Ingest.fs) |
| Live WebSocket fan-out (`/ws`)                          | [src/edge/Hub.fs](src/edge/Hub.fs) |
| Threshold alert engine                                  | [src/edge/Alerts.fs](src/edge/Alerts.fs) |
| Console + WebSocket + webhook + Slack delivery          | [src/edge/Notify.fs](src/edge/Notify.fs) |
| HTTP Basic auth gate on `/ingest/*` (per-token)         | [src/edge/Auth.fs](src/edge/Auth.fs) |
| Single-page dark dashboard                              | [src/edge/wwwroot/index.html](src/edge/wwwroot/index.html) |
| Query API (`/api/metrics`, `/api/metrics/<n>`, `/api/logs`) | [src/edge/Query.fs](src/edge/Query.fs) |

## Where it's going

Read [PLAN.md](PLAN.md). TL;DR: a multi-tenant SaaS that speaks OTLP /
Prometheus `remote_write` / Loki push, embeds Mimir/Loki/Tempo for
storage, and competes on cost transparency, predictable pricing, and
sub-second alerts.

## Repository layout (target — see [PLAN.md](PLAN.md))

```
.
├── src/
│   ├── edge/             # F#/Suave: ingest, query, alerting, notify (THIS is today's PulseBoard)
│   ├── control-plane/    # tenants, identity, billing, admin API
│   └── ui/               # dashboards, onboarding, account UI
├── infra/                # Terraform, Helm, Dockerfiles, CI deploy bits
├── docs/                 # MkDocs / Docusaurus source for the public docs site
├── PLAN.md
├── README.md
└── LICENSE
```

The edge service code now lives under [src/edge/](src/edge/). The
sibling `src/control-plane/` and `src/ui/` trees are still stubs to be
filled per [PLAN.md](PLAN.md).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Security issues: see
[SECURITY.md](SECURITY.md) — please do **not** open a public issue.

## License

PulseBoard is licensed under the [GNU Affero General Public License v3.0
or later](LICENSE). If you operate a modified version on a network
service, AGPL §13 requires you to offer the modified source to its users.

A commercial license (for proprietary embedding or hosted resale without
AGPL obligations) will be available alongside PulseBoard Cloud — open a
discussion if you need one before launch.

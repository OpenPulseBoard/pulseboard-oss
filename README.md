# PulseBoard

> Real-time metrics, logs, and alerts — a fast, opinionated observability platform.

PulseBoard started as a single-binary F#/Suave demo and has grown into
a multi-tenant observability runtime that speaks OTLP / Prometheus
`remote_write` / Loki push, embeds Mimir/Loki/Tempo for storage, and
emits sub-second alerts.

This repository is the OSS workspace/runtime product.

| Target | Project / image | Used for |
| --- | --- | --- |
| OSS workspace runtime | [`src/edge/PulseBoard.fsproj`](src/edge/PulseBoard.fsproj), [`Dockerfile`](Dockerfile) | Self-hosted PulseBoard. CI publishes the image to GitHub Container Registry under the repository owner's namespace. |

| | |
| --- | --- |
| **Status** | Pre-alpha. APIs and storage formats can change without notice. |
| **License** | [AGPL-3.0-or-later](LICENSE). Commercial licenses available on request. |
| **Language** | F# on .NET 10 |
| **HTTP stack** | [Suave](https://suave.io) |

---

## Quick start

The commands below are for the OSS workspace runtime.

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
| Self-host landing, operator sign-in, pricing page        | [src/edge/wwwroot/home.html](src/edge/wwwroot/home.html), [src/edge/wwwroot/operator-signin.html](src/edge/wwwroot/operator-signin.html), [src/edge/wwwroot/pricing.html](src/edge/wwwroot/pricing.html) |

## Where it's going

Multi-tenant ingest, embedded Mimir/Loki/Tempo storage, and operator
workflows (dashboards, alert routing, on-call, status pages). See
[docs/](docs/) for the per-feature design notes.

## Repository layout

```
.
├── src/
│   └── edge/             # F#/Suave: ingest, query, alerting, notify, dashboards
├── infra/                # Helm charts, Mimir / Loki / Tempo configs, runbooks
├── docs/                 # Feature guides and HTTP API reference
├── tests/                # Unit, integration, chaos, and load tests
├── Dockerfile
├── docker-compose.yml
├── README.md
└── LICENSE
```

For operators, the practical ownership in this repo is straightforward:

- [src/edge/](src/edge/) is the OSS/runtime product.
- [Dockerfile](Dockerfile) packages the workspace runtime.
- [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) covers self-host and workspace-image usage.
- [docs/README.md](docs/README.md) indexes the feature guides and HTTP API reference.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Security issues: see
[SECURITY.md](SECURITY.md) — please do **not** open a public issue.

## License

PulseBoard is licensed under the [GNU Affero General Public License v3.0
or later](LICENSE). If you operate a modified version on a network
service, AGPL §13 requires you to offer the modified source to its users.

A commercial license (for proprietary embedding or hosted resale without
AGPL obligations) is available on request — open a discussion if you
need one.

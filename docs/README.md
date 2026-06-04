# PulseBoard Documentation

PulseBoard is a single-binary observability edge service: it ingests metrics,
logs and traces, evaluates alert rules, routes notifications, and serves a
built-in dashboard SPA. Everything in this folder documents the **OSS edge
runtime** under [src/edge/](../src/edge/).

## Operator guides

- [DEPLOYMENT.md](DEPLOYMENT.md) — self-host and workspace-image deployment.

## Feature guides

- [features/README.md](features/README.md) — index of all feature guides.
- [features/alerting.md](features/alerting.md) — **how alerts are generated and how rules are edited.**
- [features/ingestion.md](features/ingestion.md) — all supported ingest protocols.
- [features/querying-and-dashboards.md](features/querying-and-dashboards.md) — query APIs, dashboards and panels.
- [features/notifications-and-oncall.md](features/notifications-and-oncall.md) — routing, silences, on-call and notifications.
- [features/runbooks.md](features/runbooks.md) — inline runbooks and post-incident analytics.
- [features/multi-tenancy-and-auth.md](features/multi-tenancy-and-auth.md) — tenants, API keys, RBAC, OIDC SSO and audit.
- [features/storage-and-retention.md](features/storage-and-retention.md) — storage backends, retention and rollups.

## Reference

- [reference/configuration.md](reference/configuration.md) — every CLI flag and environment variable.
- [reference/http-api.md](reference/http-api.md) — the complete HTTP endpoint directory.

## Quick mental model

```
                +---------- ingest ----------+
 apps / agents -| /ingest, /api/v1/write,    |
                | /v1/*, /loki/*, /rum/*      |
                +--------------+--------------+
                               |
                               v
                     metric / log / span stores
                               |
        +----------------------+----------------------+
        v                      v                      v
   query APIs            rule evaluator           dashboards
 (/api/metrics,         (every group's        (/api/dashboards,
  /api/prom, ...)        intervalMs)            SPA panels)
                               | fires AlertInstance
                               v
        routing -> silences -> inhibition -> mute -> grouping
                               |
                               v
              escalation (on-call) -> notification queue
                               |
                               v
            Slack / webhook / PagerDuty / ... receivers
```

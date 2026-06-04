# Feature Guides

These guides document the features implemented in the PulseBoard OSS edge
runtime ([src/edge/](../../src/edge/)). They describe behaviour that exists in
the code today — not roadmap items.

| Guide | What it covers |
|-------|----------------|
| [alerting.md](alerting.md) | How alert rules are defined, edited, evaluated, and turned into alert instances. **Start here if you are confused about how alerts are generated.** |
| [ingestion.md](ingestion.md) | Every supported ingest protocol (native JSON, Prometheus remote-write, OTLP, Loki push, scrape, RUM). |
| [querying-and-dashboards.md](querying-and-dashboards.md) | Native query API, Prometheus/Loki query APIs, dashboards and panel types. |
| [notifications-and-oncall.md](notifications-and-oncall.md) | Routing tree, silences, inhibitions, mute windows, on-call schedules, escalation, the notification queue and DLQ. |
| [runbooks.md](runbooks.md) | Inline markdown runbooks, checklist tracking, and post-incident MTTR analytics. |
| [multi-tenancy-and-auth.md](multi-tenancy-and-auth.md) | Single- vs multi-tenant mode, API keys, RBAC scopes, OIDC SSO, audit logging. |
| [storage-and-retention.md](storage-and-retention.md) | Embedded vs external (Mimir/Loki/Tempo/S3/Postgres) backends, retention and rollups. |

For an exhaustive flag list see [../reference/configuration.md](../reference/configuration.md).
For the full endpoint directory see [../reference/http-api.md](../reference/http-api.md).

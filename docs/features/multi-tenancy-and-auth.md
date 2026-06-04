# Multi-tenancy, auth and RBAC

PulseBoard runs in one of two modes. The mode determines how requests are
authenticated and whether the admin surface exists.

Source: [Tenancy.fs](../../src/edge/Tenancy.fs), [Rbac.fs](../../src/edge/Rbac.fs),
[Auth.fs](../../src/edge/Auth.fs), [Admin.fs](../../src/edge/Admin.fs),
[Oidc.fs](../../src/edge/Oidc.fs), [Audit.fs](../../src/edge/Audit.fs),
[Signup.fs](../../src/edge/Signup.fs).

## Single-tenant vs multi-tenant

| | Single-tenant (default) | Multi-tenant (`--multi-tenant`) |
|--|--|--|
| Tenant | One synthetic tenant `__local__` | Many tenants, each with a slug |
| Auth | HTTP Basic via `--tokens-file=` (open if none, with a warning) | Scoped API keys or OIDC session |
| Admin API (`/api/admin/*`) | Not mounted | Mounted, requires Admin scope |
| RBAC / scopes | None | Enforced |
| Bootstrap | Just start it | Requires `--seed-tenant=<slug>` |

## Tenant model

```
Tenant = { id: opaque-id; slug: "^[a-z][a-z0-9-]{2,31}$"; plan: Free|Pro|Enterprise; createdAt }
```

Plans set default quota envelopes (e.g. Free ≈ 10k active series; Pro ≈ 250k +
SSO; Enterprise contract-based). Per-tenant quota overrides are configurable —
see [storage-and-retention.md](storage-and-retention.md) and the admin API below.

## API keys and scopes

API keys are issued per tenant. The plaintext form is `pk_<id>.<secret>` and is
shown **once** at creation; only a salted hash (PBKDF2-SHA256 or Argon2id) is
stored.

A key has a **role** that maps to a set of **scopes**:

| Role | Scopes |
|------|--------|
| Viewer | Query |
| Editor | Ingest + Query |
| Admin | Ingest + Query + Admin |
| Billing | (none — billing UI only) |

### Scope → endpoint gates

| Scope | Protects |
|-------|----------|
| `Ingest` | `/ingest/*`, `/api/v1/write`, `/api/prom/push`, `/v1/*`, `/loki/api/v1/push` |
| `Query` | `/api/metrics*`, `/api/logs`, `/api/prom/api/v1/*`, `/api/loki/api/v1/*`, `/api/dashboards*`, `GET /api/rules*`, `GET /api/alertmanager/config`, `/api/alerts*`, `/api/traces*`, `/api/servicemap`, `/api/runbooks/*` |
| `Admin` | `/api/admin/*`, `/api/secrets/*`, `POST/PUT/DELETE /api/rules*`, `PUT /api/alertmanager/config`, silences, on-call, notify queue |

Send a key as a bearer token: `Authorization: Bearer pk_<id>.<secret>`.

## Admin API

All under `/api/admin/*` (multi-tenant only, Admin scope):

| Method | Path | Purpose |
|--------|------|---------|
| `GET` | `/api/admin/tenants` | List tenants. |
| `POST` | `/api/admin/tenants` | Create a tenant (`{"slug": "..."}`). |
| `PATCH` | `/api/admin/tenants/{id}/plan` | Change plan. |
| `GET`/`POST` | `/api/admin/tenants/{id}/api-keys` | List / issue API keys. |
| `GET` | `/api/admin/tenants/{id}/users` | List OIDC SSO users. |
| `PATCH` | `/api/admin/users/{id}` | Change a user's role. |
| `GET`/`PUT` | `/api/admin/tenants/{id}/quotas` | Read / set quota overrides. |
| `GET` | `/api/admin/tenants/{id}/cardinality` | Active-series cardinality. |
| `GET`/`PUT` | `/api/admin/tenants/{id}/retention` | Read / set retention overrides. |
| `GET` | `/api/admin/tenants/{id}/scrape-targets` … | Manage Prometheus scrape targets. |
| `GET` | `/api/admin/tenants/{id}/listeners` … | Manage StatsD/Carbon listeners. |
| `GET` | `/api/admin/tenants/{id}/usage` | Current-period usage. |
| `POST` | `/api/admin/billing/flush` | Force a usage rollup. |
| `GET` | `/api/admin/tenants/{id}/cost/series` | Top series by estimated cost. |
| `GET` | `/api/admin/tenants/{id}/cost/teams` | Cost aggregated by team. |
| `GET` | `/api/admin/audit` | Recent audit events (`tail=N`). |

## OIDC browser SSO

Mounted only when `--oidc-issuer=`, `--oidc-client-id=` and
`--oidc-redirect-uri=` are all set (multi-tenant only):

| Method | Path | Purpose |
|--------|------|---------|
| `GET` | `/auth/login` | Start the OIDC flow (PKCE if no client secret). |
| `GET` | `/auth/callback` | Exchange the code, mint a session cookie. |
| `GET` | `/auth/logout` | Clear the session. |
| `GET` | `/auth/me` | Current principal (cookie, bearer, or Basic). |

Role assignment on first login: an existing `(issuer, sub)` reuses its stored
role; otherwise an email match against `--oidc-admins/editors/viewers/billing=`
wins; otherwise `--oidc-default-role=` applies (deny if unset). Sessions are
HS256 JWTs in an httpOnly `pulse_session` cookie.

## Public sign-up

In multi-tenant mode an unauthenticated self-serve sign-up is available:

| Method | Path | Purpose |
|--------|------|---------|
| `POST` | `/api/signup` | Create a tenant + admin key (`{"slug","email"}`). |
| `GET` | `/api/wizard/snippets` | Onboarding code snippets. |
| `POST` | `/api/bootstrap/keys` | Issue a key with `--bootstrap-token=` (provisioner use). |

Sign-up validates the slug (`^[a-z][a-z0-9-]{2,31}$`, reserved names rejected)
and rate-limits to 5 sign-ups per IP per hour. Every attempt is audited.

## Audit logging

In multi-tenant mode every request records an audit event (timestamp, tenant,
api-key id, action, resource path, Allow/Deny outcome, remote IP, details).
Events are stored in an in-memory ring (most recent 1024), optionally mirrored
to Postgres (`pb_audit_events`), and optionally exported nightly to S3
(`--audit-s3-bucket=`, requires `--postgres=`).

## Secrets and PII

Admin-scoped secret/PII endpoints support envelope encryption (AES-256-GCM with
a per-tenant DEK wrapped by a master KEK):

| Method | Path | Purpose |
|--------|------|---------|
| `POST` | `/api/secrets/encrypt` | Encrypt a value. |
| `POST` | `/api/secrets/decrypt` | Decrypt a value. |
| `GET`/`PUT` | `/api/secrets/policy` | Read / set PII auto-encryption patterns. |

The master key lives at `<dataDir>/secrets/master.key` (or `PULSE_MASTER_KEY`),
per-tenant DEKs alongside it (or Postgres `pb_deks`).

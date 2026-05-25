# Deploying PulseBoard

PulseBoard is **one binary, two roles**: a per-workspace runtime (single-tenant
or hosted multi-tenant) and a public marketing site. This document covers how
to deploy each, and sketches the provisioning system we are planning for
auto-subdomain per-customer workspaces.

---

## 1. Topology overview

```
┌────────────────────────────┐      ┌────────────────────────────────┐
│  pulseboard.cloud          │      │  acme.pulseboard.cloud         │
│  (public marketing site)   │      │  (one customer's workspace)    │
│                            │      │                                │
│  /        landing          │      │  /app    dashboard SPA         │
│  /docs    documentation    │      │  /admin  keys / billing / RBAC │
│  /pricing rate-card UI     │      │  /ingest data plane            │
│  /signup  → provisioner    │      │  /v1/*   OTLP, Prom, Loki      │
│  /signin  → workspace URL  │      │                                │
└─────────────┬──────────────┘      └────────────────────────────────┘
              │
              │ POST /api/signup
              ▼
        ┌─────────────┐         spins up a workspace and returns
        │ provisioner │ ──────► its URL + first API key
        └─────────────┘
```

Today, a single multi-tenant deployment runs the *whole* topology in one
process (marketing + edge data plane + many tenant workspaces, distinguished
by Bearer token rather than hostname). The "provisioner box" in the diagram
above is **not built yet** — it is the Phase 9 work tracked in `PLAN.md`.

---

## 2. Deployment scenarios

### 2.1 Local development (you, on a laptop)

```bash
git clone https://github.com/<org>/pulseboard
cd pulseboard/src/edge
dotnet run
# Server on http://127.0.0.1:8080
```

This is **single-tenant**: there is no `/api/signup`. Use `--seed-tenant=<slug>`
(if your build has it) or set up keys via the admin UI. Both the marketing
pages and the dashboard are served; in dev you can ignore the conceptual
split.

### 2.2 Self-hosted single workspace (one team, one process)

```bash
PULSE_DATA_DIR=/var/lib/pulseboard \
  dotnet PulseBoard.dll --port=8080
```

Front with `nginx` / Caddy for TLS. One key set, one team. Suitable when you
own the box and the data should never leave it.

### 2.3 Self-hosted multi-tenant edge (one process, many workspaces)

```bash
PULSE_DATA_DIR=/var/lib/pulseboard \
  dotnet PulseBoard.dll \
    --multi-tenant \
    --port=8080 \
    --postgres="Host=db;Database=pulse;Username=pulse"
```

`/api/signup` is now live. Each tenant is identified by its Bearer key; the
marketing site and the data plane share the same host. This is what we run in
CI and the smoke tests.

### 2.4 Hosted product (Fly Machines + Caddy)

Three logical tiers, all from the same binary. See §6 for the full
details.

| Tier | Binary | Hostname | What it does |
| --- | --- | --- | --- |
| Marketing | `pulseboard --site-only` | `pulseboard.cloud` | Serves only `/`, `/docs`, `/pricing`, `/signup`, `/signin`. Proxies `POST /api/signup` to the provisioner. |
| Provisioner | `pulseboard --mode=provisioner` | internal (e.g. `provisioner.flycast`) | Allocates a subdomain, spawns a Fly Machine, bootstraps the first API key, answers Caddy's on-demand TLS questions. |
| Workspace | `pulseboard --multi-tenant` (one Fly app per customer) | `<slug>.pulseboard.cloud` | Real ingest + dashboard + admin. |

---

## 3. Front-end / TLS

Whichever scenario you pick, the binary listens on a single HTTP port on
loopback. Production should put a TLS terminator in front:

### Caddy (minimal)

```caddy
pulseboard.cloud, *.pulseboard.cloud {
    reverse_proxy 127.0.0.1:8080
}
```

### nginx

```nginx
server {
    listen 443 ssl http2;
    server_name pulseboard.cloud *.pulseboard.cloud;
    ssl_certificate     /etc/letsencrypt/live/pulseboard.cloud/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/pulseboard.cloud/privkey.pem;

    # WebSocket upgrade for /ws (live dashboards)
    location /ws {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_read_timeout 1h;
    }
    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_set_header Host              $host;
        proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

---

## 4. Environment variables

| Var | Purpose | Default |
| --- | --- | --- |
| `PULSE_DATA_DIR` | On-disk storage root | `./data` |
| `PULSE_OIDC_AUTHORITY` | Optional OIDC issuer for SSO | unset |
| `PULSE_OIDC_CLIENT_ID` | OIDC client id | unset |
| `PULSE_OIDC_CLIENT_SECRET` | OIDC client secret | unset |
| `PULSE_NOTIFY_SMTP_HOST` | SMTP host for alert notifications | unset |
| `PULSE_NOTIFY_SMTP_USER` | SMTP user | unset |
| `PULSE_NOTIFY_SMTP_PASS` | SMTP password | unset |
| `PULSE_EDGE_SECRET` | HMAC secret for `/_internal/v1/*` gateway | unset |

CLI flags worth knowing:

| Flag | Effect |
| --- | --- |
| `--port=N` | HTTP port (default 8080) |
| `--multi-tenant` | Enables `/api/signup` and tenant-isolation in stores |
| `--postgres="…"` | Use Postgres for tenant/key/audit storage |
| `--edge-secret=…` | Pair this process with a separate storage role |
| `--role=storage\|edge\|all` | Split storage from edge (advanced) |

---

## 5. Docker (illustrative)

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/edge -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
ENV PULSE_DATA_DIR=/data
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "PulseBoard.dll", "--port=8080"]
```

Run:

```bash
docker run --rm -p 8080:8080 -v pulse-data:/data \
  pulseboard:latest --multi-tenant
```

---

## 6. Provisioning per-customer subdomains

Implemented on **Fly Machines** + **Caddy on-demand TLS**. Three moving
pieces, all from the same binary:

```
                  pulseboard.cloud (Caddy in front)
                          │
              ┌───────────┴───────────┐
              ▼                       ▼
     pulseboard --site-only    pulseboard --mode=provisioner
     (apex marketing host)     (slug → Fly Machine registry)
              │                       │ POST Fly Machines API
              │ POST /api/signup      ▼
              └──── proxied ────►  spawns pb-<slug> Fly app
                                  serving multi-tenant edge
                                  on https://pb-<slug>.fly.dev
```

### 6.1 Marketing host — `pulseboard --site-only`

Serves only `/`, `/home`, `/docs`, `/pricing`, `/signup`, `/signin`. Has
no tenant store, no quota state, no ingest or query routes. POST
`/api/signup` is forwarded verbatim to `--provisioner-url=…`.

```bash
dotnet PulseBoard.dll --site-only \
  --port=8080 \
  --provisioner-url=http://provisioner.internal:8080
# or: PULSE_PROVISIONER_URL=http://… dotnet PulseBoard.dll --site-only
```

If `--provisioner-url` is not set the static pages still work; signup
attempts return HTTP 503 with a clear error.

### 6.2 Provisioner — `pulseboard --mode=provisioner`

Three endpoints:

| Endpoint | Caller | Purpose |
| --- | --- | --- |
| `POST /api/provision` | marketing host | allocates slug, spawns Fly Machine, bootstraps first key, returns `{slug, url, apiKey, tenantId, apiKeyId}` |
| `GET /provision/ask?domain=<host>` | Caddy on-demand TLS | 200 if slug is known, 404 otherwise (gates cert issuance) |
| `GET /provision/route?domain=<host>` | Caddy dynamic upstreams | returns `{"upstream":"https://pb-<slug>.fly.dev"}` |

Flags / env:

| Flag | Env | Default |
| --- | --- | --- |
| `--root-domain=` | `PULSE_ROOT_DOMAIN` | `pulseboard.cloud` |
| `--fly-token=` | `FLY_API_TOKEN` | *(required unless `--dry-run`)* |
| `--fly-org=` | `FLY_ORG_SLUG` | *(required unless `--dry-run`)* |
| `--fly-region=` | `PULSE_FLY_REGION` | `iad` |
| `--workspace-image=` | `PULSE_WORKSPACE_IMAGE` | `registry.fly.io/pulseboard1:latest` |
| `--postgres=` | `PULSE_POSTGRES` | when set, slug→workspace registry persists in table `pb_workspaces` (schema auto-created). Without it the registry is in-memory and a provisioner restart forgets every allocation. Recommended for any non-dev deploy. |
| `--dry-run` | — | when set, logs intent and returns synthetic IDs without calling Fly. Useful for local smoke tests. |

Run live:

```bash
FLY_API_TOKEN=fo1_… FLY_ORG_SLUG=pulseboard \
  dotnet PulseBoard.dll --mode=provisioner --port=8080 \
    --root-domain=pulseboard.cloud \
    --workspace-image=registry.fly.io/pulseboard1:latest
```

Run dry (no credentials needed, useful for testing the marketing flow):

```bash
dotnet PulseBoard.dll --mode=provisioner --dry-run --port=19001
# In another shell:
dotnet PulseBoard.dll --site-only --port=19002 \
  --provisioner-url=http://127.0.0.1:19001
curl -X POST http://127.0.0.1:19002/api/signup \
  -H 'content-type: application/json' \
  -d '{"slug":"acme","email":"alice@acme.co"}'
# {"slug":"acme","url":"https://acme.pulseboard.cloud",
#  "tenantId":"tenant_…","apiKey":"pk_…","apiKeyId":"key_…"}
```

The registry is in-memory in the first cut; restarting the provisioner
forgets allocations. A Postgres-backed `IWorkspaceRegistry` is straightforward
to add (interface is in `Provisioner.fs`); not in this slice.

### 6.3 Caddy in front — on-demand TLS for `*.pulseboard.cloud`

Use [`infra/cloud/Caddyfile`](../infra/cloud/Caddyfile) as the starting point. Key
ideas:

- `on_demand_tls { ask {$PROVISIONER_URL}/provision/ask }` makes Caddy
  query the provisioner before minting a cert. Unknown subdomains
  return 404 and Caddy declines — Let's Encrypt is never hammered for
  random hostnames.
- `reverse_proxy { dynamic http url {$PROVISIONER_URL}/provision/route?domain={http.request.host} }`
  resolves the upstream lazily on every request (cached 30 s).
- The apex (`pulseboard.cloud`) is a normal `reverse_proxy` to the
  `--site-only` host. Two env vars wire it together:

```bash
PROVISIONER_URL=http://provisioner.flycast \
SITE_URL=http://site.flycast \
  caddy run --config /etc/caddy/Caddyfile
```

### 6.4 Workspace bootstrap — security note

After the Fly Machine boots, the provisioner makes one `POST /api/signup`
to it to mint the first key. There is a small window between machine
boot and that POST during which someone else could race the call.
Hardening to do **before** running this in production:

1. Pass a single-use bootstrap secret to the new Machine via env, and
   gate `/api/signup` behind `X-Bootstrap-Token: <secret>` until the
   first key is issued.
2. Flip a "bootstrapped=true" flag on the workspace so subsequent
   `/api/signup` calls require operator action.

Both are small additions in `Signup.fs` — punted to keep this slice
focused on the provisioning plumbing.

### 6.5 What still has to be designed

- **Workspace teardown** (cancellation, scale-to-zero, evict-on-inactive).
- **DNS automation** if you don't use Fly's automatic `<app>.fly.dev`
  hostnames (e.g. you want every customer on a Route53 wildcard record
  pointing at a single Caddy box).
- **Postgres-backed registry** so the slug→app mapping survives a
  provisioner restart.
- **Bootstrap-secret hardening** described in 6.4.

---

## 7. Customer portal, auth, and billing (Phase 10)

Phase 10 layers a customer-facing tier on top of the §6 provisioning
stack. The marketing host (`--site-only`) gains:

- `/signup`, `/signin`, `/portal` — customer SPA pages.
- `/api/auth/*` — email/password + GitHub OAuth, issues a
  `pb_access` (1h HS256 JWT, `aud=pulseboard-portal`) plus a
  `pb_refresh` (30d opaque) cookie.
- `/api/portal/*` — customer-authenticated CRUD over their workspaces.
- `/api/stripe/webhook` — Stripe → apex (HMAC-verified).
- `/api/portal/internal/heartbeat` — workspace → apex, used by the
  free-tier idle sleeper.

The provisioner endpoints (§6.2) are unchanged except that
`POST /api/provision` is now driven by the portal API, not by
anonymous `/api/signup`.

### 7.1 New environment variables

| Var | Purpose | Default |
| --- | --- | --- |
| `PULSE_PUBLIC_BASE` | Public apex URL, e.g. `https://pulseboard.cloud`; used in OAuth + Stripe redirects | required |
| `PULSE_AUTH_JWT_SECRET` | HS256 signing key for `pb_access` | required (no portal if unset) |
| `PULSE_AUTH_GITHUB_CLIENT_ID` / `_SECRET` | GitHub OAuth App credentials | optional (button hidden if unset) |
| `PULSE_AUTH_SMTP_HOST` / `_PORT` / `_USER` / `_PASS` / `_FROM` | Email-verification + receipt SMTP | optional (verification skipped in dev) |
| `PULSE_STRIPE_SECRET_KEY` | `sk_live_…` or `sk_test_…` | optional (no billing if unset) |
| `PULSE_STRIPE_WEBHOOK_SECRET` | `whsec_…` matched against `Stripe-Signature` | required if Stripe configured |
| `PULSE_STRIPE_PRICE_STARTER_MONTHLY` | Stripe Price id, mapped to Starter plan | required if Stripe configured |
| `PULSE_STRIPE_PRICE_STARTER_ANNUAL` | Optional annual price | optional |
| `PULSE_STRIPE_PRICE_PRO_MONTHLY` | Stripe Price id, mapped to Pro plan | required if Stripe configured |
| `PULSE_STRIPE_PRICE_PRO_ANNUAL` | Optional annual price | optional |
| `PULSE_FREE_SLEEP_DAYS` | Days idle before a free workspace is auto-archived; `0` disables | `7` |
| `PULSE_FREE_SLEEP_INTERVAL_MIN` | Sleeper sweep cadence | `60` |
| `PULSE_FREE_SLEEP_MAX_PER_TICK` | Safety cap on archives per sweep | `50` |
| `PULSE_PURGE_DAYS` | Days a workspace stays archived before permanent purge; `0` disables | `30` |
| `PULSE_OVERDUE_GRACE_DAYS` | Days a non-entitled paid subscription stays live before archive; `0` disables | `3` |
| `PULSE_PURGE_INTERVAL_MIN` | PurgeCron cadence (shared by overdue + purge passes) | `360` |
| `PULSE_PURGE_MAX_PER_TICK` | Safety cap on each pass per tick | `20` |

Equivalent CLI flags exist for every var (`--stripe-secret-key=`,
`--free-sleep-days=`, etc.); CLI wins over env. The portal is enabled
iff `PULSE_AUTH_JWT_SECRET` + `PULSE_PROVISIONER_TOKEN` +
`PULSE_POSTGRES` are all set.

### 7.2 Stripe webhook setup

1. In the Stripe dashboard, create an endpoint pointing at
   `${PULSE_PUBLIC_BASE}/api/stripe/webhook`.
2. Subscribe to `checkout.session.completed`,
   `customer.subscription.created`, `customer.subscription.updated`,
   `customer.subscription.deleted`, `invoice.payment_failed`.
3. Copy the signing secret into `PULSE_STRIPE_WEBHOOK_SECRET`.
4. The webhook handler is the source of truth for plan upgrades — a
   customer is **not** promoted to a paid plan until Stripe sends
   `customer.subscription.created` / `.updated` with `status=active`.

Pinned Stripe API version: `2024-12-18.acacia`.

### 7.3 Free-tier idle sleep

Free workspaces auto-archive after `PULSE_FREE_SLEEP_DAYS` days
without ingest activity. The mechanism:

1. The workspace edge posts a heartbeat to
   `${PULSE_PUBLIC_BASE}/api/portal/internal/heartbeat` on ingest
   (rate-limited to ~1/min per slug).
2. The heartbeat endpoint updates
   `pb_customer_workspaces.last_active_at` for that slug.
3. A background sweeper on apex queries
   `WHERE plan='free' AND status='live' AND last_active_at <= NOW() - threshold`
   and POSTs `archive` to the provisioner, then marks the row
   `Archived` with `error='auto-archived after N days idle'`.
4. The customer can unarchive from `/portal` at any time, which
   resumes the Fly machine and resets `last_active_at`.

**Wiring on the workspace side:** the provisioner sets three env vars
on every new Fly machine — `PULSE_APEX_HEARTBEAT_URL`,
`PULSE_APEX_HEARTBEAT_TOKEN`, `PULSE_WORKSPACE_SLUG`. Apex feeds
these from `PULSE_APEX_PUBLIC_URL` and `PULSE_APEX_HEARTBEAT_TOKEN`
(or, if unset, `PULSE_PROVISIONER_TOKEN`). See
[infra/runbooks/portal-and-billing.md §3](../infra/runbooks/portal-and-billing.md)
for verification steps and failure modes.

### 7.4 Operations

See [infra/runbooks/portal-and-billing.md](../infra/runbooks/portal-and-billing.md)
for day-2 operations: verifying the sleeper, inspecting idle
workspaces, manually waking, Stripe webhook health, customer
tear-down, and failure-mode recovery.



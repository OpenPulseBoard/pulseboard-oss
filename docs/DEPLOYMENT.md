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

### 2.4 Hosted product (the target architecture)

> **Status:** designed but not built. See `PLAN.md` Phase 9.

Three logical tiers, each a deployment of the same binary or a thin extra
service:

| Tier | Binary | Hostname | What it does |
| --- | --- | --- | --- |
| Marketing | `pulseboard --site-only` *(flag not yet implemented)* | `pulseboard.cloud` | Serves only `/`, `/docs`, `/pricing`, `/signup`, `/signin`. Proxies `POST /api/signup` to the provisioner. |
| Provisioner | new service | `provisioner.internal` | Allocates a subdomain, creates DNS, starts (or assigns) a workspace runtime, returns `{ url, apiKey }`. |
| Workspace | `pulseboard --multi-tenant` (or single-tenant per pod) | `<slug>.pulseboard.cloud` | Real ingest + dashboard + admin for one (or many) tenants. |

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

## 6. Provisioning per-customer subdomains (planned)

This is the Phase 9 work. The intended flow:

1. Visitor signs up on `https://pulseboard.cloud/signup`.
2. The marketing host proxies `POST /api/signup` to the provisioner.
3. The provisioner:
   - Allocates a slug (`acme-7f3a`) and registers DNS
     (`acme-7f3a.pulseboard.cloud` → workspace pool ingress).
   - Creates a tenant row in the central Postgres.
   - Issues a first API key.
   - Returns `{ url: "https://acme-7f3a.pulseboard.cloud", apiKey: "pk_…" }`.
4. The marketing `signup.html` redirects the user to the returned `url/app`
   with the key pre-stashed.

Open design choices (see `PLAN.md` Phase 9) we still need to commit to:

- **Workspace runtime:** dedicated process per tenant (one container per
  customer) vs. shared multi-tenant edges with hostname routing vs. a
  pool of workers picked by a router.
- **Orchestrator:** Kubernetes, Fly Machines, Nomad, plain systemd + a
  control plane daemon, or "all customers on one big multi-tenant
  process and route by Bearer". Each has very different cost and
  isolation properties.
- **DNS:** wildcard cert + dynamic A records via the cloud DNS API, or
  per-customer Caddy on-demand TLS.
- **Storage:** shared Postgres with tenant column (cheap, what we have
  today) vs. one Postgres per workspace (expensive, fully isolated).

The marketing site, the dashboard, and the data plane do **not** change in
any of these scenarios — only the deployment shape around them does.

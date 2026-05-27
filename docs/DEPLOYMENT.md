# Deploying PulseBoard OSS

This document covers the OSS workspace/runtime deployment surface in this
repository. Hosted marketing, provisioner, portal, and billing deployment
details live with the private cloud repo after the split.

---

## 1. Deployment scenarios

### 1.1 Local development

```bash
git clone https://github.com/<org>/pulseboard
cd pulseboard/src/edge
dotnet run
# Server on http://127.0.0.1:8080
```

This is the OSS workspace runtime. Use `--seed-tenant=<slug>` if you want
pre-created tenant data, or create keys via the admin UI.

### 1.2 Self-hosted single workspace

```bash
PULSE_DATA_DIR=/var/lib/pulseboard \
  dotnet PulseBoard.dll --port=8080
```

Front with `nginx` / Caddy for TLS. One key set, one team. Suitable when you
own the box and the data should never leave it.

### 1.3 Self-hosted multi-tenant workspace runtime

```bash
PULSE_DATA_DIR=/var/lib/pulseboard \
  dotnet PulseBoard.dll \
    --multi-tenant \
    --port=8080 \
    --postgres="Host=db;Database=pulse;Username=pulse"
```

`/api/signup` is now live. Each tenant is identified by its Bearer key; the
workspace runtime serves the same app, ingest, query, admin, and docs routes
for every tenant.

---

## 2. Front-end / TLS

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

## 3. Environment variables

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

## 4. Container image

| Image | Dockerfile | Entrypoint | Used for |
| --- | --- | --- | --- |
| Workspace image | [`../Dockerfile`](../Dockerfile) | `dotnet PulseBoard.dll` | Self-hosted OSS deployments and the workspace image consumed by the hosted control plane. Published as `registry.fly.io/pulseboard1`. |

### 4.1 Workspace image

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

## 5. Contract boundary for hosted consumers

The hosted control plane is no longer implemented in this repository,
but it still consumes the workspace image and HTTP endpoints documented
in [`CONTRACT.md`](CONTRACT.md).

That contract exists so the OSS workspace repo can be public while the
hosted control plane remains private.

### 7.4 Operations

See [infra/runbooks/portal-and-billing.md](../infra/runbooks/portal-and-billing.md)
for day-2 operations: verifying the sleeper, inspecting idle
workspaces, manually waking, Stripe webhook health, customer
tear-down, and failure-mode recovery.



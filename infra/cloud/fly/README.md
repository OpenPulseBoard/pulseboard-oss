# Fly.io deployment configs

Four Fly apps make up the hosted PulseBoard topology. There are three
container shapes:

- `cloud.Dockerfile` builds the hosted control-plane image used by the
    marketing site and provisioner.
- `Dockerfile` builds the OSS workspace image used by each `pb-<slug>` app.
- `caddy.Dockerfile` builds the public TLS/front-door image.

CI publishes those application images to Fly registry as:

- `registry.fly.io/pulseboard1:latest` for workspaces.
- `registry.fly.io/pulseboard-cloud:latest` for the site and provisioner.

```
                    Internet
                        │
                  ┌─────▼─────┐
                  │  pulseboard-caddy │  public IPv4/IPv6
                  │  (caddy.toml)     │  TLS terminator, on-demand certs
                  └─┬───────────┬─────┘
        flycast :8080│           │flycast :8080
                  ┌──▼──┐    ┌───▼───────────────┐
                  │site │    │provisioner        │
                  │.toml│    │.toml              │
                  └─────┘    └──┬────────────────┘
                                │ Fly Machines API
                                ▼
                  ┌────────────────────────────────┐
                  │ pb-<slug>  (workspace.toml)    │
                  │ one app per customer           │
                  └────────────────────────────────┘
```

| File | App | Public? | Notes |
| --- | --- | --- | --- |
| [`caddy.toml`](caddy.toml) | `pulseboard-caddy` | yes (80 + 443) | Holds the Caddyfile from [`../Caddyfile`](../Caddyfile). On-demand TLS for `*.pulseboard.cloud`. |
| [`site.toml`](site.toml) | `pulseboard-site` | flycast-only | `dotnet PulseBoard.Cloud.dll --site-only`. Marketing + signup proxy. |
| [`provisioner.toml`](provisioner.toml) | `pulseboard-provisioner` | flycast-only | `dotnet PulseBoard.Cloud.dll --mode=provisioner`. Holds Fly API token + Postgres. |
| [`workspace.toml`](workspace.toml) | `pb-<slug>` (one per customer) | flycast-only | `dotnet PulseBoard.dll --multi-tenant` template the provisioner clones via the Fly Machines API. |

## Deploy order (first time)

```bash
# 1. Postgres for the provisioner (or use an external one).
fly postgres create --name pulseboard-pg --region iad

# 2. Registry backing apps for prebuilt images.
fly apps create pulseboard1
fly apps create pulseboard-cloud

# 3. Provisioner.
fly apps create pulseboard-provisioner
fly postgres attach pulseboard-pg -a pulseboard-provisioner
fly secrets set -a pulseboard-provisioner \
    FLY_API_TOKEN=fo1_… \
    FLY_ORG_SLUG=pulseboard
fly deploy -a pulseboard-provisioner --config infra/cloud/fly/provisioner.toml

# 4. Marketing site.
fly apps create pulseboard-site
fly secrets set -a pulseboard-site \
    PULSE_PROVISIONER_URL=http://pulseboard-provisioner.flycast
fly deploy -a pulseboard-site --config infra/cloud/fly/site.toml

# 5. Caddy front door — last, because it depends on the two above.
fly apps create pulseboard-caddy
fly ips allocate-v4 -a pulseboard-caddy
fly ips allocate-v6 -a pulseboard-caddy
fly volumes create caddy_data --size 1 --region iad -a pulseboard-caddy
fly secrets set -a pulseboard-caddy \
    PROVISIONER_URL=http://pulseboard-provisioner.flycast \
    SITE_URL=http://pulseboard-site.flycast
fly deploy -a pulseboard-caddy --config caddy.toml

# 6. Point DNS at the Caddy app's IPs (apex + wildcard A/AAAA).
fly ips list -a pulseboard-caddy
```

The `pb-<slug>` apps are NOT created with `fly deploy` — the
provisioner creates them via the Fly Machines API on each
`POST /api/provision`. `workspace.toml` documents the canonical
machine config and lets you manually launch a canary workspace with
`fly launch --copy-config --name pb-canary`.

## Smoke after first deploy

```bash
curl -fsSL https://pulseboard.cloud/ | head -20             # hosted home
curl -fsSL https://pulseboard.cloud/pricing | head -20
curl -fsSL -X POST https://pulseboard.cloud/api/signup \
    -H 'content-type: application/json' \
    -d '{"slug":"canary","email":"ops@pulseboard.cloud"}'   # provisions pb-canary
curl -fsSL https://canary.pulseboard.cloud/healthz          # workspace alive
```

## What lives elsewhere

- Application code (workspace, provisioner, site-only) — see
  [`../../../src/edge/`](../../../src/edge/) and
  [`../../../src/cloud/`](../../../src/cloud/).
- Caddyfile — [`../Caddyfile`](../Caddyfile).
- Operator runbooks (failover, TLS rotation) —
  [`../../runbooks/`](../../runbooks/).

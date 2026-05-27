# Cloud / Workspace Contract

This document defines the supported integration boundary between the
hosted control plane in `src/cloud/` and the OSS workspace runtime in
`src/edge/`.

Rule: the cloud side may depend on published workspace images and the
HTTP endpoints below, but not on workspace source files, shared storage
internals, or linked F# modules.

## Version

Current contract version: `2026-05-27`.

The current contract is intentionally small and stable enough to carry
the repo split. If request or response shapes change, bump this version
and update both repos together.

## Workspace image contract

- The cloud side consumes a published workspace image, not workspace
  source.
- Current default workspace image: `registry.fly.io/pulseboard1:latest`.
- The image reference is passed through `PULSE_WORKSPACE_IMAGE` or
  `--workspace-image=` on the provisioner.
- Recommended post-split policy: pin tested version tags instead of
  deploying against `:latest`.

## Workspace-owned endpoints

These routes are implemented by the workspace runtime and are the only
supported cloud -> workspace control-plane calls today.

### `POST /api/signup`

Purpose: bootstrap a freshly provisioned workspace by creating the first
tenant and first admin API key.

Caller: hosted site or provisioner, depending on deployment flow.

Request body:

```json
{
  "slug": "acme",
  "email": "alice@acme.co"
}
```

Success response: `201 Created`

```json
{
  "tenantId": "tenant_...",
  "slug": "acme",
  "plan": "free",
  "apiKey": "pk_...",
  "apiKeyId": "key_...",
  "wizardUrl": "/onboard?key=...&tenant=...",
  "warning": "plaintext apiKey is shown once and cannot be recovered"
}
```

Failure classes:

- `400` invalid JSON or missing fields
- `409` slug already taken
- `429` signup rate-limited

### `POST /api/bootstrap/keys`

Purpose: mint a replacement admin API key for an already provisioned
workspace, primarily for the customer portal.

Auth: bearer token. The presented bearer must match the workspace's
`PULSE_BOOTSTRAP_TOKEN`.

Request body:

```json
{
  "tenantId": "tenant_...",
  "label": "customer portal"
}
```

Success response: `201 Created`

```json
{
  "tenantId": "tenant_...",
  "apiKeyId": "key_...",
  "apiKey": "pk_...",
  "warning": "plaintext apiKey is shown once and cannot be recovered"
}
```

Failure classes:

- `400` invalid JSON or missing `tenantId`
- `401` missing or invalid bearer token
- `404` tenant not found

## Cloud-owned endpoints the workspace calls

These routes are implemented by the cloud side and are the supported
workspace -> cloud control-plane calls today.

### `POST /api/portal/internal/heartbeat`

Purpose: update last-seen activity for one or more hosted workspaces so
the apex can drive idle sleep and portal status.

Caller: workspace runtime.

Auth: bearer token. The workspace presents `PULSE_APEX_HEARTBEAT_TOKEN`;
the cloud side currently validates it against the provisioner token.

Request body:

```json
{
  "slug": "acme"
}
```

or

```json
{
  "slugs": ["acme", "beta"]
}
```

Success response: `200 OK`

```json
{
  "received": 1
}
```

Failure classes:

- `400` invalid JSON or missing `slug` / `slugs`
- `401` bad bearer token
- `503` heartbeat disabled because the cloud side has no provisioner token configured

Related env contract on the workspace side:

- `PULSE_APEX_HEARTBEAT_URL`
- `PULSE_APEX_HEARTBEAT_TOKEN`
- `PULSE_WORKSPACE_SLUG`

### `GET /provision/ask`

Purpose: Caddy asks whether a hostname is allowed before minting a cert.

Query:

```text
?domain=<slug>.pulseboard.cloud
```

Success semantics:

- `200` known hostname
- `404` unknown hostname

### `GET /provision/route`

Purpose: Caddy resolves a hostname to the current workspace upstream.

Query:

```text
?domain=<slug>.pulseboard.cloud
```

Success response: `200 OK`

```json
{
  "upstream": "https://pb-acme.fly.dev"
}
```

## Split rule

After the repo split:

- the OSS repo owns the implementation of workspace endpoints and the
  workspace image publication;
- the private cloud repo owns the implementation of provisioner, site,
  portal, billing, and cloud deployment assets;
- any new cross-repo behavior must be added here before code in either
  repo depends on it.
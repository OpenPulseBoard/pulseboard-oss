# `src/cloud/` — hosted control plane

Code in this directory is **commercial / SaaS-only** and is destined for a
separate private repository (`pulseboard/cloud`) before the OSS edge is
released publicly. It is kept in-tree today to make the OSS/SaaS boundary
visible while the provisioner contract is still iterating; the unified
[`src/edge/PulseBoard.fsproj`](../edge/PulseBoard.fsproj) links these files
in via `<Compile>` / `<None>` items with `<Link>` so today's single binary
keeps working without configuration changes.

## What lives here

| File | Purpose |
| --- | --- |
| [`Provisioner.fs`](Provisioner.fs) | `--mode=provisioner` Suave service: `POST /api/provision`, Caddy `ask` / `route` endpoints, `IFlyClient` (real + dry-run). |
| [`PgWorkspaceRegistry.fs`](PgWorkspaceRegistry.fs) | Postgres-backed `IWorkspaceRegistry` (`pb_workspaces` table) so provisioner allocations survive restart. |
| [`SiteOnly.fs`](SiteOnly.fs) | `--site-only` mode: marketing routes only, proxies `POST /api/signup` to the provisioner. |
| `wwwroot/home.html` | Public landing page for `pulseboard.cloud`. |
| `wwwroot/signup.html` | Hosted self-serve signup flow. |
| `wwwroot/signin.html` | Hosted sign-in form. |
| `wwwroot/pricing.html` | Public pricing calculator. |
| [`../../infra/cloud/Caddyfile`](../../infra/cloud/Caddyfile) | Wildcard TLS + on-demand `ask` + dynamic upstream config for the hosted edge. |

## What does **not** belong here

Anything a self-hoster should be able to run, fork, or audit lives in
[`src/edge/`](../edge/) — ingest, query, alerts, dashboards, RBAC,
retention, traces, RUM, AI assist (Echo), secrets, OIDC, on-call,
billing meter + `FileBillingProvider`, plan catalog. Those are the
product. The cloud directory is purely the *hosted business* around it.

## Contract to OSS

The cloud binaries (provisioner + site-only) interact with OSS workspace
binaries only over versioned JSON HTTP:

- `POST /api/signup` on a freshly-provisioned workspace (one-shot
  bootstrap; to be hardened with a single-use secret env).
- `GET /provision/{ask,route}` served by the provisioner; consumed by
  Caddy on the edge.
- Future: Stripe webhook receiver in cloud, calling
  `PATCH /api/admin/tenants/<id>/plan` and reading
  `GET /api/admin/tenants/<id>/usage` on each workspace.

No shared F# library exists between cloud and OSS. The contract is the
HTTP surface and the on-disk shapes (`pb_workspaces` table,
`billing/events.jsonl` rollups).

## Step 2 — repo split

When the OSS repo is flipped public:

1. `git filter-repo --path src/cloud/ --path infra/cloud/` into a new
   private `pulseboard/cloud` repo.
2. Drop the four `<Compile Include="..\cloud\…">` and four
   `<None Include="..\cloud\wwwroot\…">` entries from
   `src/edge/PulseBoard.fsproj` in the OSS repo.
3. Drop the `--mode=provisioner` and `--site-only` early-dispatch
   branches from `src/edge/Program.fs`.
4. Add a minimal stub `src/edge/wwwroot/home.html` to the OSS repo so
   `/` still resolves (e.g. "Your self-hosted PulseBoard — go to
   `/app` or `/docs`").
5. Cloud repo's Dockerfile pulls `pulseboard/pulseboard:latest` as the
   workspace image, builds its own provisioner/site-only binary from a
   sibling fsproj that depends only on Suave + Npgsql + the four files
   originally moved here.

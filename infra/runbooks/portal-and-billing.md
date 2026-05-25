# Runbook: Customer portal & billing

**Scope:** Operate the Phase 10 customer-facing layer — signup/signin,
the member portal at `/portal`, Stripe billing, and the free-tier idle
sleeper. Covers initial setup, day-2 operations, and known failure
modes.

See [PLAN.md → Phase 10](../../PLAN.md) for the design and
[docs/DEPLOYMENT.md §7](../../docs/DEPLOYMENT.md) for the deployment
wiring.

---

## Topology recap

```
   pulseboard.cloud (apex, site-only)
   ├── /                                   marketing pages
   ├── /signup, /signin, /portal           customer SPA + APIs
   ├── /api/auth/*                         customer auth (email + GitHub)
   ├── /api/portal/*                       portal data (customer JWT)
   ├── /api/stripe/webhook                 Stripe → apex
   └── /api/portal/internal/heartbeat      workspace → apex (provisioner bearer)
                  │
                  │  apex calls provisioner with PULSE_PROVISIONER_TOKEN
                  ▼
   provisioner ── creates / archives / unarchives per-workspace Fly apps
                  ▼
   <slug>.pulseboard.cloud (one Fly app per customer)
```

Apex owns four Postgres tables (all `pb_*`):

- `pb_customers` — login identity, password hash, email-verified flag,
  github user id.
- `pb_customer_workspaces` — slug ↔ customer, plan, status, the new
  `last_active_at` column used by the sleeper.
- `pb_customer_stripe_links` — customer ↔ Stripe customer id.
- `pb_stripe_subscriptions` — slug ↔ Stripe subscription id, current
  status (active / past_due / canceled / etc).

The workspace Postgres schema (`pb_<slug>`) is unchanged; the
customer-facing layer is fully additive on the apex side.

---

## 1. Initial setup

### 1.1 Required environment

Apex (`--site-only`) needs the union of all of these. Anything marked
optional disables a feature but does not break the binary.

| Var | Purpose | Required? |
| --- | --- | --- |
| `PULSE_POSTGRES` | Customer + workspace + Stripe link tables | yes |
| `PULSE_PROVISIONER_URL` | Where to reach provisioner over flycast | yes |
| `PULSE_PROVISIONER_TOKEN` | Admin bearer; also gates the heartbeat endpoint | yes |
| `PULSE_ROOT_DOMAIN` | Used to build customer-visible URLs | yes |
| `PULSE_PUBLIC_BASE` | e.g. `https://pulseboard.cloud`, used in OAuth redirect | yes |
| `PULSE_AUTH_JWT_SECRET` | HS256 signing key for `pb_access` | yes |
| `PULSE_AUTH_GITHUB_CLIENT_ID` / `_SECRET` | GitHub OAuth | optional |
| `PULSE_AUTH_SMTP_*` | SMTP for email verification + receipts | optional |
| `PULSE_STRIPE_SECRET_KEY` | `sk_live_…` or `sk_test_…` | optional (no billing if unset) |
| `PULSE_STRIPE_WEBHOOK_SECRET` | `whsec_…` shared with Stripe | required if Stripe configured |
| `PULSE_STRIPE_PRICE_STARTER_MONTHLY` | Stripe Price id | required if Stripe configured |
| `PULSE_STRIPE_PRICE_STARTER_ANNUAL` | Stripe Price id | optional |
| `PULSE_STRIPE_PRICE_PRO_MONTHLY` | Stripe Price id | required if Stripe configured |
| `PULSE_STRIPE_PRICE_PRO_ANNUAL` | Stripe Price id | optional |
| `PULSE_FREE_SLEEP_DAYS` | Days idle before auto-archive (0 disables) | default `7` |
| `PULSE_FREE_SLEEP_INTERVAL_MIN` | Sweep cadence | default `60` |
| `PULSE_FREE_SLEEP_MAX_PER_TICK` | Safety cap per sweep | default `50` |
| `PULSE_PURGE_DAYS` | Days archived before permanent purge (0 disables) | default `30` |
| `PULSE_OVERDUE_GRACE_DAYS` | Days an unpaid subscription stays live before archive (0 disables) | default `3` |
| `PULSE_PURGE_INTERVAL_MIN` | Cron cadence shared by both purge passes | default `360` (6h) |
| `PULSE_PURGE_MAX_PER_TICK` | Safety cap per pass per tick | default `20` |

Equivalent CLI flags exist for every env var
(`--stripe-secret-key=`, `--free-sleep-days=`, etc.); see
`Program.fs` for the exact list. CLI wins over env.

### 1.2 Database migrations

Schema creation is idempotent and runs on apex startup. To preview
exactly what will be applied:

```bash
psql "$PULSE_POSTGRES" -c "\d pb_customer_workspaces"
```

The `last_active_at TIMESTAMPTZ NOT NULL DEFAULT NOW()` column was
added in Phase 10 step 7; older rows are back-filled to `now()` at
ALTER time, so existing free workspaces effectively get a grace
period equal to `PULSE_FREE_SLEEP_DAYS` after the upgrade — by
design.

### 1.3 Stripe configuration

1. **Create products in the Stripe dashboard.** Two products
   (`Starter`, `Pro`), each with at least a monthly Price. Annual
   prices are optional but recommended.
2. **Copy Price ids** (`price_…`) into the env vars listed above.
3. **Configure a webhook endpoint** pointing at
   `https://pulseboard.cloud/api/stripe/webhook`. Subscribe to:
   - `checkout.session.completed`
   - `customer.subscription.created`
   - `customer.subscription.updated`
   - `customer.subscription.deleted`
   - `invoice.payment_failed`
4. **Copy the webhook signing secret** (`whsec_…`) into
   `PULSE_STRIPE_WEBHOOK_SECRET`.
5. **Smoke-test** with the Stripe CLI:
   ```bash
   stripe listen --forward-to https://pulseboard.cloud/api/stripe/webhook
   stripe trigger checkout.session.completed
   ```
   Apex should log `[stripe-webhook] event ev_… type=checkout.session.completed ok`.

The webhook handler is the source of truth for plan upgrades. The
`POST /api/portal/workspaces/<slug>/plan` endpoint kicks off a Stripe
Checkout session; the customer is not promoted to a paid plan until
Stripe confirms the subscription via webhook. If the webhook is
mis-configured, customers will appear to pay but their workspace will
stay on Free — check this first whenever a customer complains about
"paid but not upgraded".

### 1.4 GitHub OAuth (optional)

Register an OAuth App with callback
`https://pulseboard.cloud/auth/github/callback`. Set the two env vars.
GitHub login is offered alongside email/password on `/signin`; the two
identities are merged automatically when the GitHub-verified email
matches an existing `pb_customers.email`.

---

## 2. Day-2 operations

### 2.1 Verifying the free-tier sleeper is running

On apex startup, look for:

```
  Sleeper:       free-tier idle archive every 01:00:00 after 7.00:00:00 of inactivity (max 50/tick)
```

If you see `Sleeper: disabled …` instead, one of:

- `PULSE_FREE_SLEEP_DAYS=0` (intentional kill-switch).
- `PULSE_PROVISIONER_TOKEN` unset (sleeper has no way to issue archive
  calls).
- Building the `portal` config failed (look earlier in the boot log
  for `auth disabled` or `store unavailable`).

To force a sweep without waiting an hour, restart apex with
`--free-sleep-interval-min=1` and watch for `[sleeper] N idle free
workspace(s) past …`.

### 2.2 Inspecting idle free workspaces

```sql
SELECT slug, customer_id, last_active_at, NOW() - last_active_at AS idle_for
  FROM pb_customer_workspaces
 WHERE plan = 'free' AND status = 'live'
 ORDER BY last_active_at ASC
 LIMIT 20;
```

Workspaces that get archived by the sleeper have `error` set to
`auto-archived after N days idle` — useful for distinguishing
auto-archive from customer-initiated archive in audit reviews.

### 2.3 Manually waking an archived workspace

A customer can unarchive themselves from `/portal`. If they cannot
(e.g. their browser is broken), an operator can:

```bash
curl -X POST -H "Authorization: Bearer $PULSE_PROVISIONER_TOKEN" \
  https://provisioner.internal/admin/workspaces/<slug>/unarchive
```

then `UPDATE pb_customer_workspaces SET status='live',
  archived_at=NULL, last_active_at=NOW() WHERE slug='<slug>';`.

### 2.4 Disabling the sleeper temporarily

Set `PULSE_FREE_SLEEP_DAYS=0` and restart apex. The portal and
heartbeat endpoint continue to work; only the periodic archive
loop is suppressed. Useful during a launch / migration when you
explicitly don't want to evict anyone.

### 2.5 Adjusting the safety cap

`PULSE_FREE_SLEEP_MAX_PER_TICK` (default 50) limits how many
workspaces the sleeper will archive in one pass. If a misconfiguration
caused `last_active_at` to skew for many workspaces, this cap gives
you time to notice before the entire free fleet is suspended. Raise
it once you trust your activity signal; lower it (e.g. to 5) when
you first turn the sleeper on in production.

### 2.6 Stripe webhook health

`pb_stripe_subscriptions.updated_at` is bumped on every webhook
delivery. If the most recent row is more than ~6 hours old in a
production system, something is wrong:

```sql
SELECT MAX(updated_at) FROM pb_stripe_subscriptions;
```

Common causes:

- Webhook endpoint URL changed without updating Stripe.
- `PULSE_STRIPE_WEBHOOK_SECRET` rotated on one side only — webhook
  HMAC verification will fail with `bad signature` log lines.
- Caddy / Fly proxy returning a 502 because apex is down — Stripe
  will retry, but only for ~3 days before giving up. Re-deliver from
  the Stripe dashboard if you see gaps.

---

## 3. Workspace-side heartbeat caller

**Status: shipped (Phase 10 step 9).**

Each workspace Fly app pings apex on ingest so the sleeper's
`last_active_at` reflects actual customer activity, not
time-since-creation.

Wiring on the apex side (already covered in §2): the provisioner
injects three env vars when it bootstraps a new Fly machine:

| Env var | Value | Source |
| --- | --- | --- |
| `PULSE_APEX_HEARTBEAT_URL` | apex public base URL, e.g. `https://pulseboard.cloud` | apex `PULSE_APEX_PUBLIC_URL` (or `--apex-public-url=`) |
| `PULSE_APEX_HEARTBEAT_TOKEN` | bearer; must match apex `PULSE_PROVISIONER_TOKEN` | apex `PULSE_APEX_HEARTBEAT_TOKEN`, defaults to `PULSE_PROVISIONER_TOKEN` |
| `PULSE_WORKSPACE_SLUG` | the workspace slug being bootstrapped | set automatically per-machine |

When any of `URL`/`TOKEN`/`SLUG` is unset (self-hosted or
single-tenant deploys), `HeartbeatClient.init` receives `None` and
the heartbeat is a permanent no-op.

`src/edge/HeartbeatClient.fs` holds a process-global
`lastSentTicks : int64 mutable` updated via
`Interlocked.CompareExchange`. `bump ()` is called from every
accepted ingest payload (Ingest, OTLP metrics/logs/traces,
PromRemoteWrite, LokiPush) and POSTs `{"slug":"<slug>"}` to
`${apexUrl}/api/portal/internal/heartbeat` at most once per
`PULSE_APEX_HEARTBEAT_INTERVAL_MIN` minutes (default 1). The first
transport failure is logged to stderr; subsequent failures are
suppressed.

To verify in production:

1. `flyctl -a pb-<slug> ssh console -C "env | grep PULSE_APEX"`
   — all three vars present.
2. Push a single OTLP/Prom-RW payload to the workspace.
3. `psql $PULSE_APEX_PG -c "SELECT last_active_at FROM pb_workspaces
   WHERE slug='<slug>'"` — should be within the last minute.

If step 3 doesn't tick, check apex logs for
`[portal] heartbeat slug=<slug> 401` (token mismatch) or
workspace stderr for `[heartbeat] first POST failed`.

---

## 4. PurgeCron: archive→purge and payment_overdue → archive

**Status: shipped (Phase 10 step 10).**

A single periodic loop on apex (`src/cloud/PurgeCron.fs`) runs two
independent passes each tick:

- **`overduePass`** — Stripe webhooks set
  `pb_customer_workspaces.overdue_since` when a paid workspace's
  subscription stops being entitled (`canceled` / `unpaid` /
  `incomplete_expired`). The pass archives any workspace whose
  `overdue_since` is older than `PULSE_OVERDUE_GRACE_DAYS`
  (default `3`). The workspace is kept on its old paid plan during
  the grace window so a transient card decline doesn't brick a
  customer mid-incident.
- **`purgePass`** — workspaces in `Archived` state for longer than
  `PULSE_PURGE_DAYS` (default `30`) are permanently destroyed via
  the provisioner's `POST /admin/workspaces/<slug>/purge`
  endpoint. That tears down the Fly app, drops the per-workspace
  Postgres schema (`pb_<slug>`), and removes the registry row;
  apex then hard-deletes the `pb_customer_workspaces` row.

Either pass is disabled by setting its threshold to `0`. The whole
cron is also auto-disabled when the apex has no provisioner
token (same gating as the sleeper).

### 4.1 Verifying the cron is running

Look for this single line in apex stdout at startup:

```
  PurgeCron:     every 06:00:00 (purge after 30.00:00:00, overdue grace 3.00:00:00, max 20/tick/pass)
```

When a pass actually acts on a row you'll see one of:

```
  [overdue] N workspace(s) past 3.00:00:00 grace; archiving M
  [overdue] <slug> archived (overdue 4 day(s))
  [purge]   N archived workspace(s) past 30.00:00:00; purging M
  [purge]   <slug> purged (archived for 31.04:12:00)
```

### 4.2 Recovering a workspace marked overdue by mistake

If a customer paid but the webhook hasn't fired yet (Stripe queue
backed up), they can still sign in. The webhook will eventually
clear `overdue_since` automatically. If you need to clear it
immediately:

```sql
UPDATE pb_customer_workspaces
   SET overdue_since = NULL, updated_at = NOW()
 WHERE slug = '<slug>';
```

If the cron already archived them, follow §2.3 (manual unarchive).

### 4.3 Disabling either pass

- `PULSE_PURGE_DAYS=0` — never auto-purge. Archived workspaces
  stay forever; manual cleanup via `flyctl apps destroy pb-<slug>`
  + `DELETE FROM pb_customer_workspaces`.
- `PULSE_OVERDUE_GRACE_DAYS=0` — never auto-archive due to
  payment failure. Useful during a billing migration when you
  don't want to evict anyone mid-rollout.

### 4.4 Adjusting the safety cap

`PULSE_PURGE_MAX_PER_TICK` (default `20`) caps each pass per tick.
A misconfiguration that ages many workspaces past the threshold
won't nuke the whole fleet in one pass; you'll have at least one
tick interval (`PULSE_PURGE_INTERVAL_MIN`, default 6h) to notice
the runaway log line and react.

---

## 5. Failure modes & recovery

| Symptom | Likely cause | Recovery |
| --- | --- | --- |
| Customer reports "paid but still Free" | Stripe webhook not configured / wrong secret | Verify `pb_stripe_subscriptions` has a recent row; if not, redeliver from Stripe dashboard. Once delivered the row appears and the next portal refresh shows the new plan. |
| `/portal` returns 401 immediately after signin | `PULSE_AUTH_JWT_SECRET` rotated; old cookie invalid | Customer signs in again. To force a rotation for all customers, change the secret and restart apex. |
| Sleeper archives an active customer | Heartbeat env vars missing on the workspace machine (§3) — workspace silently fell back to no-op | Unarchive (§2.3), verify `PULSE_APEX_HEARTBEAT_URL/TOKEN` + `PULSE_WORKSPACE_SLUG` are present in the machine env, restart workspace. |
| `[sleeper] archive <slug> failed: HTTP 502 …` | Provisioner unreachable | Check provisioner Fly app status; the sleeper will retry next tick, no manual intervention needed unless it's persistent. |
| `[overdue] <slug> failed: HTTP …` | Provisioner unreachable or workspace already archived (409) | Next tick retries; check provisioner logs if persistent. |
| `[purge] <slug> failed: HTTP 409 must archive before purge` | Race: workspace was unarchived between scan and purge call | Benign; row will be re-evaluated next tick. |
| Customer paid but workspace still flagged overdue in `/portal` | Stripe webhook delayed or `pb_stripe_subscriptions` row missing | Inspect `pb_stripe_subscriptions.status` for the slug; redeliver the latest subscription event from Stripe dashboard. Manual `UPDATE pb_customer_workspaces SET overdue_since=NULL` if you need it cleared immediately. |
| GitHub login button missing | `PULSE_AUTH_GITHUB_CLIENT_ID` not set | Expected; email/password still works. |
| Email verification never arrives | SMTP not configured | Operator can `UPDATE pb_customers SET email_verified_at=NOW() WHERE email='…'` for a stuck customer; long-term, configure SMTP. |

---

## 5. Tear-down for a single customer

Hard delete (GDPR / explicit close-account request):

```sql
BEGIN;
-- 1. Cancel any Stripe subscriptions out-of-band first.
SELECT stripe_subscription_id FROM pb_stripe_subscriptions
 WHERE slug IN (SELECT slug FROM pb_customer_workspaces
                 WHERE customer_id = '<cid>');
-- 2. Archive + purge each workspace via the provisioner admin API.
--    (DELETE FROM pb_customer_workspaces will cascade-clean
--     pb_stripe_subscriptions, but the Fly machine + workspace
--     Postgres schema must be destroyed separately.)
DELETE FROM pb_customers WHERE id = '<cid>';
COMMIT;
```

The ON DELETE CASCADE on `pb_customer_workspaces.customer_id`
removes the slug ownership rows. The corresponding Fly apps and
workspace Postgres schemas must be destroyed via the provisioner's
admin API (`POST /admin/workspaces/<slug>/destroy` — not exposed in
the portal; operator-only).

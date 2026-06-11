# Runbooks

Inline runbooks attach a markdown checklist to an alert rule so
responders can work through remediation steps directly from a firing alert, with
per-step completion tracking and post-incident MTTR analytics.

Source: [Runbooks.fs](../../src/edge/Runbooks.fs),
[PgRunbookStore.fs](../../src/edge/PgRunbookStore.fs), [Rules.fs](../../src/edge/Rules.fs).

## Authoring a runbook

A runbook is just the `runbook` field on a rule (see [alerting.md](alerting.md)).
Write it as GitHub-flavoured markdown; checklist items become trackable steps:

```json
{
  "id": "cpu-high",
  "name": "cpu-high",
  "lang": "promql",
  "expr": "cpu", "cmp": ">", "threshold": 0.9, "forMs": 30000,
  "severity": "warning",
  "labels": {}, "annotations": {},
  "runbook": "## CPU high\n\n- [ ] Check top CPU consumers\n- [ ] Confirm it's not a deploy/batch job\n- [ ] Scale out or shed load if sustained"
}
```

### Step extraction

Steps are parsed from the markdown body in this priority order:

1. **Task-list items** — `- [ ] text` / `- [x] text` (these win if present).
2. **Ordered list items** — `1. text` (used only if there are no task items).
3. **Bullet items** — `- text` (used only if there are no task or ordered items).

Each extracted line becomes one tracked step.

## Tracking during an incident

When an alert fires, a runbook **progress record** is created on first access,
keyed by the alert's `fingerprint`. Responders tick steps off as they go.

| Method | Path | Purpose |
|--------|------|---------|
| `GET` | `/api/alerts/{fingerprint}/runbook` | Get the runbook + current progress for an alert. |
| `POST` | `/api/alerts/{fingerprint}/runbook/step` | Mark a step complete (or undo). |
| `GET` | `/api/runbooks/incidents` | Post-incident analytics across all alerts. |

Step completion body:

```json
{ "idx": 0, "done": true, "user": "alice" }
```

- `idx` — zero-based step index (must be in range).
- `done` — `true` (default) marks complete; `false` un-marks it.
- `user` — who completed it (defaults to `operator`).

Each completion records a `pulse_runbook_step_seconds` self-metric measuring the
time from the alert firing to that step being checked off — this feeds MTTR
analytics.

## Progress lifecycle

- A record is created the first time the runbook is fetched for a firing alert
  (`firedAt`, `startedAt` stamped, `completions` empty).
- Ticking a step adds/removes a `{idx, at, user}` completion.
- When the alert resolves, the record's `resolvedAt` is stamped, which makes it
  available to incident analytics.

## Post-incident analytics

`GET /api/runbooks/incidents` groups resolved incidents by rule and reports:

- incident count,
- average MTTR (mean time to resolve),
- per-step completion stats and any **skipped steps** (steps never checked).

This is useful for spotting runbooks whose steps are routinely skipped (a sign
the runbook needs editing) or rules with consistently slow resolution.

## Storage

| Mode | Location |
|------|----------|
| File (default) | `<dataDir>/runbooks/<tenantId>.ndjson` — append-only, latest line per fingerprint wins. |
| Postgres | Table `pb_runbook_progress` (`PgRunbookStore`), keyed by `(tenant_id, fingerprint)`. |

The backend is chosen automatically: Postgres when `--postgres=` is set,
otherwise the NDJSON file store.

## In the UI

The dashboard **Alert list** panel shows a **Runbook** button per active alert.
It opens a modal that renders the checklist, posts step toggles to
`/api/alerts/{fingerprint}/runbook/step`, shows a progress bar and MTTR, and
offers an **Acknowledge** action (which posts to `/api/alerts/{fingerprint}/ack`).

## In notifications

When a fired alert carries a runbook, the outbound notification envelope
includes a `runbooks` array with the rule name, a truncated excerpt, and a deep
link back to the alert. Set `--public-url=` (or `PULSE_PUBLIC_URL`) so the deep
link is absolute (`<public-url>/#/alerts/<fingerprint>`); otherwise it is
relative.

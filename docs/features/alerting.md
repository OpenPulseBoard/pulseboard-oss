# Alerting: how alerts are generated and how rules are edited

This is the guide to read if it is unclear *where alerts come from*. It walks
the full path from a stored rule to a delivered notification.

Source: [Rules.fs](../../src/edge/Rules.fs), [Routing.fs](../../src/edge/Routing.fs),
[OnCall.fs](../../src/edge/OnCall.fs), [NotifyQueue.fs](../../src/edge/NotifyQueue.fs).

## 1. The big picture

An alert is **not** something you create by hand. You create a **rule**, and the
evaluator continuously turns that rule into zero or more **alert instances**:

```
 you edit a Rule ──► evaluator runs the rule's query every intervalMs
                         │
                         ▼
            does any series/log-count breach the threshold?
                         │ yes, and sustained for forMs
                         ▼
              an AlertInstance starts Firing  ◄── this is "an alert"
                         │
                         ▼
        routing → silences → inhibition → mute → grouping → on-call
                         │
                         ▼
               notification queue → receiver (Slack, webhook, …)
```

So: **rules are the input you edit; alert instances are the output the system
generates.** You never POST an alert; you POST a rule and wait for it to fire.

## 2. The rule model

A **rule** is a boolean alarm layered on top of an embedded PromQL or LogQL
query. Rules are grouped into **rule groups**; each group has its own evaluation
cadence (`intervalMs`). One group document is stored per file at
`<dataDir>/rules/<tenantId>/<groupId>.json` (or in Postgres table `pb_rules`).

### Rule group JSON

```json
{
  "id": "default",
  "name": "default",
  "intervalMs": 15000,
  "rules": [
    {
      "id": "cpu-high",
      "name": "cpu-high",
      "lang": "promql",
      "expr": "cpu",
      "cmp": ">",
      "threshold": 0.9,
      "forMs": 30000,
      "severity": "warning",
      "labels": { "team": "infra" },
      "annotations": { "summary": "CPU sustained above 90%" },
      "runbook": "## CPU high runbook\n\n- [ ] Check top CPU consumers\n- [ ] ..."
    }
  ],
  "createdAt": "2024-01-15T10:30:00Z",
  "updatedAt": "2024-01-15T10:30:00Z"
}
```

Field reference:

| Field | Meaning |
|-------|---------|
| `lang` | `promql` or `logql` — which embedded query surface the `expr` uses. |
| `expr` | The query. For PromQL this is a **vector selector only** (e.g. `cpu`, `http_requests{service="api"}`). For LogQL it is a stream selector plus an optional line filter. The comparison is **not** written inside `expr`. |
| `cmp` | The comparison operator applied by the evaluator: one of `>`, `<`, `>=`, `<=`, `==`, `!=`. |
| `threshold` | The numeric threshold the value is compared against. |
| `forMs` | "Pending → firing" dwell time. The breach must hold continuously for this many milliseconds before the alert fires. `0` fires immediately. |
| `severity` | `info`, `warning`, `critical`, or `page`. Used by routing matchers. |
| `labels` | Extra labels attached to every alert instance from this rule (used for routing, grouping, silences). |
| `annotations` | Human context (e.g. `summary`, `description`). Free-form. |
| `runbook` | Optional markdown checklist surfaced in the UI and notifications. See [runbooks.md](runbooks.md). |

> The important mental shift: `expr` is the **selector**, and `cmp` + `threshold`
> are a **separate** comparison the evaluator applies. A PromQL rule of
> `expr: "cpu"`, `cmp: ">"`, `threshold: 0.9` means *"alert when the latest
> sample of any `cpu` series exceeds 0.9"*.

### How each language is evaluated

- **PromQL rules** — the embedded evaluator only supports vector selectors.
  Each matching series's most-recent sample is compared against `threshold`;
  one alert instance is produced per breaching label-set. Complex PromQL must be
  offloaded to Mimir (`--mimir-url=`).
- **LogQL rules** — the evaluator counts log entries matching the selector
  within the `[now - forMs, now]` window, then compares that **count** against
  `threshold`. Alert labels come from the rule's `labels` map.

## 3. How rules are edited

There are two equivalent ways: the HTTP API and the built-in SPA. Both require
the **Admin** scope in multi-tenant mode (reads require Query).

### Via the HTTP API

| Method | Path | Behaviour |
|--------|------|-----------|
| `GET` | `/api/rules` | List all rule groups for the tenant. |
| `GET` | `/api/rules/{groupId}` | Fetch one group. |
| `POST` | `/api/rules` | **Create a new group.** The server assigns a fresh `id` (any `id` you send is ignored) and returns `201` with the stored group. |
| `PUT` | `/api/rules/{groupId}` | **Upsert** the group at that id. `createdAt` is preserved if the group already existed; `updatedAt` is stamped. |
| `DELETE` | `/api/rules/{groupId}` | Remove the group. |

Example — create a group:

```bash
curl -X POST http://localhost:8080/api/rules \
  -H "Authorization: Bearer pk_<id>.<secret>" \
  -H "Content-Type: application/json" \
  -d '{
        "name": "latency-slo",
        "intervalMs": 30000,
        "rules": [{
          "name": "p99-breach",
          "lang": "promql",
          "expr": "request_duration_ms",
          "cmp": ">",
          "threshold": 500,
          "forMs": 300000,
          "severity": "critical",
          "labels": { "team": "platform" },
          "annotations": { "summary": "P99 latency exceeded 500ms" }
        }]
      }'
```

Example — edit an existing group (replace its rules):

```bash
curl -X PUT http://localhost:8080/api/rules/<groupId> \
  -H "Authorization: Bearer pk_<id>.<secret>" \
  -H "Content-Type: application/json" \
  -d @group.json
```

### Via the SPA

The dashboard SPA (`/app`) surfaces alerts through an **Alert list** panel
(`type: "alertlist"`), which reads `GET /api/alerts`. Add an Alert list panel to
a dashboard to see active alert instances and open their runbooks. Rule-group
editing is performed through the same `/api/rules` endpoints described above.

### The seeded default

On first use each tenant gets a seeded group named `default` (`intervalMs`
15000) containing a `cpu-high` rule (`expr: "cpu"`, `cmp: ">"`, `threshold:
0.9`, `forMs: 30000`) with a sample markdown runbook. Edit or delete it freely.

## 4. From rule to alert instance

Every `intervalMs`, the sharded evaluator runs each rule and produces
**alert instances**. An alert instance is what `GET /api/alerts` returns:

```json
{
  "fingerprint": "a1b2c3…",
  "ruleId": "cpu-high",
  "ruleName": "cpu-high",
  "groupId": "default",
  "severity": "warning",
  "labels": { "team": "infra", "host": "node-1" },
  "annotations": { "summary": "CPU sustained above 90%" },
  "value": 0.94,
  "state": "firing",
  "activeAt": 1705329000000,
  "firedAt": 1705329030000,
  "resolvedAt": null,
  "lastEvalAt": 1705330000000,
  "runbook": "## CPU high runbook\n- [ ] …"
}
```

### Fingerprint

The `fingerprint` is a stable hash of `rule.id` plus the sorted label-set. The
same breaching series under the same rule always produces the same fingerprint,
so acknowledgements, runbook progress, dedup and silences track it across
evaluations.

### State machine

| State | Meaning |
|-------|---------|
| `pending` | The threshold is breaching but `forMs` has not elapsed yet. **Not routed.** |
| `firing` | Breaching and sustained ≥ `forMs`. **Routed** downstream (silences, notifications, escalation). |
| `resolved` | No longer breaching. The instance lingers in firing for a short window, then transitions to resolved. |

## 5. What happens after an alert fires

Firing instances flow into the routing pipeline. This is covered in detail in
[notifications-and-oncall.md](notifications-and-oncall.md); in short:

1. **Silences** — if a silence matcher matches and `now ∈ [startsAt, endsAt)`, drop it.
2. **Inhibition** — a higher-severity firing alert can suppress lower ones sharing `equal` labels.
3. **Route tree walk** — the matched route selects a receiver, escalation policy, grouping keys and timers.
4. **Mute windows** — if the current time falls in a configured mute window, hold delivery.
5. **Grouping & dedup** — alerts are grouped by `groupBy` labels and flushed on `groupWait`/`groupInterval`.
6. **Escalation** — if a policy is attached, on-call targets are resolved per step.
7. **Notification queue** — an outbound message per receiver is enqueued, delivered with retry, and dead-lettered on exhaustion.

## 6. Related endpoints

| Method | Path | Purpose |
|--------|------|---------|
| `GET` | `/api/alerts` | Current alert instances. |
| `POST` | `/api/alerts/{fingerprint}/ack` | Acknowledge an alert (suppresses further escalation steps). |
| `GET` | `/api/alerts/{fingerprint}/acks` | List acknowledgements for an alert. |
| `GET` | `/api/alerts/{fingerprint}/runbook` | Runbook progress for an alert. |
| `POST` | `/api/alerts/{fingerprint}/runbook/step` | Mark a runbook step complete. |
| `GET` | `/api/alertmanager/config` / `PUT` | Read/replace the routing + receivers + inhibitions config. |
| `GET` | `/api/silences` / `POST` / `DELETE /api/silences/{id}` | Manage silences. |

See [../reference/http-api.md](../reference/http-api.md) for the complete list.

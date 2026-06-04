# Notifications, routing and on-call

Once an alert instance is **firing** (see [alerting.md](alerting.md)), it enters
the routing pipeline, which decides *whether*, *to whom*, and *how often* to send
a notification. This is PulseBoard's Alertmanager-equivalent.

Source: [Routing.fs](../../src/edge/Routing.fs), [OnCall.fs](../../src/edge/OnCall.fs),
[NotifyQueue.fs](../../src/edge/NotifyQueue.fs).

## Pipeline order

```
firing AlertInstance
   │
   ├─ 1. silences        — matcher + active time window → drop
   ├─ 2. inhibition      — a source alert suppresses matching targets
   ├─ 3. route tree walk — pick receiver, policy, groupBy, timers
   ├─ 4. mute windows    — current UTC time inside a window → hold
   ├─ 5. grouping/dedup  — group by labels, flush on wait/interval
   ├─ 6. escalation      — resolve on-call targets per policy step
   └─ 7. notify queue    — one outbound message per receiver, with retry
```

## Routing configuration

The whole routing config (route tree, receivers, silences, inhibitions, mute
windows) is read and replaced as one document:

| Method | Path | Purpose |
|--------|------|---------|
| `GET` | `/api/alertmanager/config` | Read the full config. |
| `PUT` | `/api/alertmanager/config` | Replace the full config. |

It is stored at `<dataDir>/routing/<tenantId>.json` (or Postgres
`pb_routing_config`). Shape:

```json
{
  "route": {
    "matchers": [{ "name": "severity", "op": "=", "value": "critical" }],
    "receiverId": "slack-1",
    "policyId": "esc-p1",
    "groupBy": ["service", "instance"],
    "groupWaitMs": 10000,
    "groupIntervalMs": 300000,
    "repeatIntervalMs": 3600000,
    "continue": false,
    "muteTimeIds": ["off-hours"],
    "children": []
  },
  "receivers": [
    { "id": "slack-1", "name": "prod", "type": "slack",
      "url": "https://hooks.slack.com/…", "secret": null, "extra": {} }
  ],
  "inhibitions": [
    { "id": "inh-1",
      "sourceMatchers": [{ "name": "severity", "op": "=", "value": "critical" }],
      "targetMatchers": [{ "name": "severity", "op": "=", "value": "warning" }],
      "equal": ["service", "instance"] }
  ],
  "muteTimes": [
    { "id": "off-hours", "name": "off-hours",
      "windows": [{ "startMinute": 0, "endMinute": 480, "daysOfWeek": 3 }] }
  ],
  "silences": []
}
```

Matcher `op` is one of `=`, `!=`, `=~`, `!~`.

### Route tree walk

Starting at the root route, children are tested in order against the alert's
labels. The first matching child wins (or the root if none match) unless
`continue` is set, which lets evaluation fall through to siblings. The matched
route supplies the receiver, escalation policy, grouping keys and timers.

### Grouping and dedup

Alerts are grouped by `(receiverId, the labels named in groupBy)`. A group is
flushed `groupWaitMs` after its first new alert, then no more often than
`groupIntervalMs`. Identical group compositions are not re-sent within
`repeatIntervalMs`.

## Silences

Silences drop matching alerts during a time window:

| Method | Path | Purpose |
|--------|------|---------|
| `GET` | `/api/silences` | List silences. |
| `POST` | `/api/silences` | Create / upsert a silence. |
| `DELETE` | `/api/silences/{id}` | Remove a silence. |

```json
{
  "matchers": [{ "name": "alertname", "op": "=~", "value": "^Cpu" }],
  "startsAt": 1705329000000,
  "endsAt": 1705332600000,
  "createdBy": "alice@example.com",
  "comment": "maintenance window"
}
```

## Inhibition and mute windows

- **Inhibition** — if any firing alert matches `sourceMatchers` and a candidate
  matches `targetMatchers` and they share equal values for the `equal` labels,
  the candidate is suppressed. Classic use: a `critical` host-down alert
  inhibits the `warning` alerts from the same host.
- **Mute windows** — recurring weekly windows (`startMinute`, `endMinute`,
  `daysOfWeek` bitmask). While "now" (UTC) is inside a referenced window, alerts
  on that route are held.

## On-call schedules and escalation

The on-call catalog holds users, schedules and escalation policies:

| Method | Path | Purpose |
|--------|------|---------|
| `GET` | `/api/oncall/catalog` | Read the catalog. |
| `PUT` | `/api/oncall/catalog` | Replace the catalog. |
| `GET` | `/api/oncall/whoison/{scheduleId}` | Who is on call right now for a schedule. |

```json
{
  "users": [
    { "id": "u1", "name": "Alice", "email": "alice@…", "receiverIds": ["slack-u1"] }
  ],
  "schedules": [
    { "id": "primary", "name": "Primary",
      "rotations": [{ "id": "r1", "members": ["u1","u2"],
                      "periodMs": 604800000, "startAt": 1704067200000 }],
      "overrides": [{ "userId": "u2", "startsAt": 0, "endsAt": 0 }] }
  ],
  "policies": [
    { "id": "esc-p1", "name": "P1",
      "steps": [
        { "delayMs": 0, "targets": [{ "type": "schedule", "id": "primary" }] },
        { "delayMs": 900000, "targets": [{ "type": "user", "id": "u1" }] }
      ] }
  ]
}
```

- **Rotation:** current on-call member = `members[floor((now - startAt) / periodMs) % members.length]`. An override wins if `startsAt ≤ now < endsAt`.
- **Escalation steps:** step 0 fires immediately; step N fires after its
  `delayMs` *if the alert is still firing and not acknowledged*. Step targets can
  be a `receiver`, a `schedule` (resolved to the current on-call user), or a
  `user` (expanded to their `receiverIds`).

## Acknowledgements

| Method | Path | Purpose |
|--------|------|---------|
| `POST` | `/api/alerts/{fingerprint}/ack` | Acknowledge — body `{"user":"name"}`. |
| `GET` | `/api/alerts/{fingerprint}/acks` | List acks for an alert. |

An ack suppresses escalation steps beyond the current one and stops group
re-sends; it is cleared when the alert resolves.

## Notification queue and DLQ

Each routed receiver produces an **outbound message** that is delivered by the
queue with retry. Exhausted messages move to a dead-letter queue (DLQ).

| Method | Path | Purpose |
|--------|------|---------|
| `GET` | `/api/notify/queue` | Pending outbound messages. |
| `GET` | `/api/notify/dlq` | Dead-lettered messages. |
| `POST` | `/api/notify/dlq/{id}/replay` | Re-queue a dead letter. |
| `DELETE` | `/api/notify/dlq/{id}` | Purge a dead letter. |

- **Retry:** exponential backoff with jitter (`base * 2^attempt + rand`), capped
  at a max backoff; default 3 attempts before DLQ.
- **Persistence:** NDJSON journals at `<dataDir>/notify/queue.ndjson` and
  `dlq.ndjson` (or Postgres `pb_outbound_messages`).

### Receiver types

`slack`, `webhook`, `hmac_webhook` (adds an HMAC-SHA256 signature header),
`pagerduty`, `opsgenie`, `discord`, `teams`, `email`.

Simple global webhooks/Slack can also be wired without the full routing config
via `--webhook=` / `--slack=` (see [../reference/configuration.md](../reference/configuration.md)).

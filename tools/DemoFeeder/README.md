# DemoFeeder

Generates fake but plausible time-series and log data and posts it to a
PulseBoard edge so the **Overview** dashboard (`cpu_usage`,
`http_requests_total`, `system.disk.used`, `Recent logs`) shows live
values.

It also emits OTLP/HTTP traces to `/v1/traces` so the **Traces** and
**Service Map** sections of a workspace are populated with realistic
cross-service calls.

```bash
# single-tenant local edge with no auth
dotnet run --project tools/DemoFeeder -- --base-url=http://127.0.0.1:8775

# workspace with an API key (Authorization: Bearer …)
dotnet run --project tools/DemoFeeder -- \
  --base-url=https://pb-workspace.pulseboard.cloud \
  --token=pbk_…

# HTTP Basic (matches --tokens-file= setups)
dotnet run --project tools/DemoFeeder -- \
  --base-url=http://127.0.0.1:8775 \
  --basic=agent1:s3cret

# burst test: 1s interval, deterministic, for 60 seconds
dotnet run --project tools/DemoFeeder -- \
  --interval-sec=1 --duration-sec=60 --seed=42 --verbose

# traces-focused run: send 12 traces per batch
dotnet run --project tools/DemoFeeder -- \
  --interval-sec=1 --traces-per-batch=12 --duration-sec=120
```

Flags: see `--help`.

## What it feeds

| Overview panel    | Series posted                                                            |
| ----------------- | ------------------------------------------------------------------------ |
| CPU load          | `cpu_usage`, `cpu_usage{host="web1"\|"web2"\|"db1"}` as smooth random walks |
| HTTP requests     | `http_requests_total{method=…,status=…}` counters (GET/POST × 200/404/500) |
| Memory used       | `system.disk.used` (bytes), `system.memory.used`                          |
| Recent logs       | 3–8 lines per batch across `web/api/worker/db` at info/warn/error         |

## Trace topology (OTLP)

Each synthetic trace follows a service path similar to a real request:

`frontend -> api -> checkout -> postgres`

and

`api -> payments`

Spans include start/end times, status (occasional errors), server/client
span kinds, and parent-child links, which is enough for both
`/api/traces` summaries and `/api/servicemap` edge/node stats.

Useful flags:

- `--traces-per-batch=N` controls OTLP trace volume per interval.
- `--no-traces` disables OTLP emission (metrics/logs only).

## What it does **not** fake

`Active alerts` (`__alerts.firing`) and `Service health`
(`__listeners.up`) are driven by the alert engine and listener registry,
not by `/ingest/*`. To populate them, configure alert rules and
listeners on the workspace itself — this tool only feeds ingest.

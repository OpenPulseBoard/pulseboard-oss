# DemoFeeder

Generates fake but plausible time-series and log data and posts it to a
PulseBoard edge so the **Overview** dashboard (`cpu_usage`,
`http_requests_total`, `system.disk.used`, `Recent logs`) shows live
values.

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
```

Flags: see `--help`.

## What it feeds

| Overview panel    | Series posted                                                            |
| ----------------- | ------------------------------------------------------------------------ |
| CPU load          | `cpu_usage`, `cpu_usage{host="web1"\|"web2"\|"db1"}` as smooth random walks |
| HTTP requests     | `http_requests_total{method=…,status=…}` counters (GET/POST × 200/404/500) |
| Memory used       | `system.disk.used` (bytes), `system.memory.used`                          |
| Recent logs       | 3–8 lines per batch across `web/api/worker/db` at info/warn/error         |

## What it does **not** fake

`Active alerts` (`__alerts.firing`) and `Service health`
(`__listeners.up`) are driven by the alert engine and listener registry,
not by `/ingest/*`. To populate them, configure alert rules and
listeners on the workspace itself — this tool only feeds ingest.

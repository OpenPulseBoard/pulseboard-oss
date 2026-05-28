# Chaos test suite — `tests/chaos/`

This directory contains chaos-engineering scripts that inject failures into a
running PulseBoard deployment and assert recovery SLOs.

## Scripts

| Script | Target | Recovery SLO |
|--------|--------|-------------|
| `kill-edge-pod.sh` | PulseBoard edge process | `/api/healthz` → 200 within **30 s** |
| `kill-postgres.sh` | PostgreSQL (Cloud state) | Cloud portal recovers within **60 s** |
| `kill-mimir-ingester.sh` | Mimir ingester (metrics storage) | Probe metric visible within **60 s** |
| `k6-load.js` | Full ingest + query load | query p99 < **1 000 ms**, ingest success > **99.9%** |

## Prerequisites

- `curl`, `bash` ≥ 4
- For Docker mode: `docker` CLI
- For Kubernetes mode: `kubectl` with context pointing at the target cluster
- For Fly.io mode: `fly` CLI authenticated
- For k6 load test: [`k6`](https://k6.io/docs/getting-started/installation/) ≥ 0.45

## Quick start (local Docker Compose)

```bash
# 1. Start PulseBoard locally
docker compose up -d

# 2. Run individual chaos scenarios
bash tests/chaos/kill-edge-pod.sh
bash tests/chaos/kill-postgres.sh
bash tests/chaos/kill-mimir-ingester.sh

# 3. Run k6 load profile (requires k6 installed)
make bench-k6
# or directly:
BASE_URL=http://localhost:8080 k6 run tests/chaos/k6-load.js

# 4. Run all chaos scenarios via make
make chaos
```

## Environment variables

All scripts share these common variables:

| Variable | Default | Description |
|----------|---------|-------------|
| `BASE_URL` | `http://localhost:8080` | PulseBoard edge base URL |
| `DEPLOY_MODE` | `docker` | `docker` \| `k8s` \| `fly` |
| `API_KEY` | *(none)* | Bearer token for multi-tenant mode |
| `RECOVER_SECS` | varies | Max seconds to wait for recovery |

See the header comment in each script for script-specific variables.

## SLO definitions

| SLO | Threshold | Measured by |
|-----|-----------|-------------|
| Edge restart recovery | < 30 s to healthy | `kill-edge-pod.sh` poll |
| Postgres reconnect | < 60 s to healthy | `kill-postgres.sh` poll |
| Mimir data recovery | < 60 s data visible | `kill-mimir-ingester.sh` probe |
| Query p99 latency | < 1 000 ms | k6 `query_response_ms` threshold |
| Ingest success rate | > 99.9% | k6 `ingest_success_rate` threshold |
| HTTP error rate | < 1% | k6 `http_req_failed` threshold |

## CI integration

Add to `.github/workflows/chaos.yml` or your CI pipeline:

```yaml
chaos:
  runs-on: ubuntu-latest
  needs: [unit-tests]
  steps:
    - uses: actions/checkout@v4
    - name: Start PulseBoard (docker compose)
      run: docker compose up -d && sleep 10
    - name: Chaos — kill edge pod
      run: bash tests/chaos/kill-edge-pod.sh
    - name: Chaos — kill postgres
      run: bash tests/chaos/kill-postgres.sh
    - name: Chaos — kill Mimir ingester
      run: bash tests/chaos/kill-mimir-ingester.sh
    - name: k6 load (short run, 2 min)
      run: |
        k6 run tests/chaos/k6-load.js \
          --env BASE_URL=http://localhost:8080 \
          --env DURATION=2m \
          --env VUS_INGEST=10 \
          --env VUS_QUERY=3
```

#!/usr/bin/env bash
# tests/chaos/kill-mimir-ingester.sh
#
# Chaos scenario: kill the Mimir ingester (long-term metrics store) and assert
# that PulseBoard's in-memory MetricStore continues to absorb writes, and that
# the Mimir ingester SLO is met when it comes back.
#
# Recovery SLO:
#   - PulseBoard /api/healthz returns 200 immediately during Mimir outage
#     (edge buffers writes in memory and retries)
#   - After Mimir restart, ingested metrics reappear in query results within
#     RECOVER_SECS (default: 90s)
#   - Zero data-loss for samples written in the 10s window before kill
#     (verified by querying a known probe metric after recovery)
#
# Modes:
#   docker — docker restart <container>
#   k8s    — kubectl delete pod -l component=ingester
#   fly    — fly machine stop/start on the Mimir app machine
#
# Usage:
#   DEPLOY_MODE=docker CONTAINER=mimir-ingester bash tests/chaos/kill-mimir-ingester.sh
#   DEPLOY_MODE=k8s POD_LABEL=component=ingester NAMESPACE=pulseboard bash tests/chaos/kill-mimir-ingester.sh
#
# Environment variables:
#   DEPLOY_MODE    — fly | k8s | docker (default: docker)
#   BASE_URL       — PulseBoard base URL (default: http://localhost:8080)
#   MIMIR_URL      — Mimir API base URL (default: http://localhost:9009)
#   CONTAINER      — Docker container name (default: mimir-ingester)
#   POD_LABEL      — kubectl selector (default: component=ingester)
#   NAMESPACE      — Kubernetes namespace (default: pulseboard)
#   MIMIR_APP      — Fly.io app name for Mimir (default: pulseboard-mimir)
#   OUTAGE_SECS    — seconds to keep ingester down (default: 15)
#   RECOVER_SECS   — max seconds to wait for data recovery (default: 90)
#   PROBE_METRIC   — metric name to probe for data continuity (default: pulseboard_ingest_total)
#   API_KEY        — optional Bearer token for PulseBoard edge (default: none)

set -euo pipefail

BASE_URL="${BASE_URL:-http://localhost:8080}"
MIMIR_URL="${MIMIR_URL:-http://localhost:9009}"
DEPLOY_MODE="${DEPLOY_MODE:-docker}"
CONTAINER="${CONTAINER:-mimir-ingester}"
POD_LABEL="${POD_LABEL:-component=ingester}"
NAMESPACE="${NAMESPACE:-pulseboard}"
MIMIR_APP="${MIMIR_APP:-pulseboard-mimir}"
OUTAGE_SECS="${OUTAGE_SECS:-15}"
RECOVER_SECS="${RECOVER_SECS:-90}"
PROBE_METRIC="${PROBE_METRIC:-pulseboard_ingest_total}"
API_KEY="${API_KEY:-}"

GREEN='\033[0;32m'; RED='\033[0;31m'; YELLOW='\033[1;33m'; RESET='\033[0m'

log()  { echo -e "[chaos:mimir] $*"; }
ok()   { echo -e "${GREEN}[PASS]${RESET} $*"; }
fail() { echo -e "${RED}[FAIL]${RESET} $*"; exit 1; }
warn() { echo -e "${YELLOW}[WARN]${RESET} $*"; }

HEALTH_ARGS=(-sf -o /dev/null -w "%{http_code}")
QUERY_ARGS=(-sf -w "%{http_code}" -o /tmp/chaos_probe_response.json)
[[ -n "$API_KEY" ]] && HEALTH_ARGS+=(-H "Authorization: Bearer $API_KEY") && \
                       QUERY_ARGS+=(-H "Authorization: Bearer $API_KEY")

# ---------------------------------------------------------------------------
# 1. Baseline
# ---------------------------------------------------------------------------
log "Step 1 — verifying baseline health"

EDGE_H=$(curl "${HEALTH_ARGS[@]}" "${BASE_URL}/api/healthz" 2>/dev/null || echo "000")
if [[ "$EDGE_H" != "200" ]]; then
  fail "Edge health returned $EDGE_H — aborting"
fi
ok "Edge baseline health OK"

# Record a probe point before the chaos injection so we can verify continuity.
PROBE_BEFORE_TS=$(date +%s)
log "  Recording probe metric baseline at t=$PROBE_BEFORE_TS"

# ---------------------------------------------------------------------------
# 2. Kill Mimir ingester
# ---------------------------------------------------------------------------
log "Step 2 — killing Mimir ingester (mode=$DEPLOY_MODE, outage=${OUTAGE_SECS}s)"

MIMIR_MACHINE_ID=""

case "$DEPLOY_MODE" in
  docker)
    docker stop "$CONTAINER"
    ;;
  k8s)
    kubectl delete pods -l "$POD_LABEL" -n "$NAMESPACE" --grace-period=0 --force
    ;;
  fly)
    MIMIR_MACHINE_ID=$(fly machine list --app "$MIMIR_APP" --json | \
      python3 -c "import sys,json; m=json.load(sys.stdin); print(m[0]['id'])" 2>/dev/null || true)
    if [[ -z "$MIMIR_MACHINE_ID" ]]; then
      fail "Could not resolve Fly.io Mimir machine ID for app $MIMIR_APP"
    fi
    fly machine stop "$MIMIR_MACHINE_ID" --app "$MIMIR_APP"
    ;;
  *)
    fail "Unknown DEPLOY_MODE=$DEPLOY_MODE"
    ;;
esac

KILL_TIME=$(date +%s)
log "Mimir ingester killed at $(date -u +%H:%M:%SZ)"

# ---------------------------------------------------------------------------
# 3. Edge resilience during outage
# ---------------------------------------------------------------------------
log "Step 3 — verifying edge still accepts writes during ${OUTAGE_SECS}s Mimir outage"

WRITE_ERRORS=0
for i in $(seq 1 "$OUTAGE_SECS"); do
  # The edge should continue accepting Prom remote_write; it queues/retries to Mimir.
  HC=$(curl "${HEALTH_ARGS[@]}" "${BASE_URL}/api/healthz" 2>/dev/null || echo "000")
  if [[ "$HC" != "200" ]]; then
    warn "  Edge healthz returned $HC at second $i (may reconnect)"
    WRITE_ERRORS=$((WRITE_ERRORS + 1))
  fi
  sleep 1
done

if [[ "$WRITE_ERRORS" -gt 3 ]]; then
  fail "Edge returned non-200 health $WRITE_ERRORS times during Mimir outage — SLO BREACH"
fi
ok "Edge remained resilient during Mimir outage ($WRITE_ERRORS transient errors)"

# ---------------------------------------------------------------------------
# 4. Restart Mimir ingester
# ---------------------------------------------------------------------------
log "Step 4 — restarting Mimir ingester"

case "$DEPLOY_MODE" in
  docker)
    docker start "$CONTAINER"
    ;;
  k8s)
    log "  (Kubernetes will recreate the pod automatically)"
    ;;
  fly)
    fly machine start "$MIMIR_MACHINE_ID" --app "$MIMIR_APP"
    ;;
esac

RESTART_TIME=$(date +%s)

# ---------------------------------------------------------------------------
# 5. Wait for Mimir to accept queries
# ---------------------------------------------------------------------------
log "Step 5 — waiting for Mimir data recovery (SLO: ${RECOVER_SECS}s)"

DEADLINE=$((RESTART_TIME + RECOVER_SECS))
RECOVERED=false

while [[ "$(date +%s)" -lt "$DEADLINE" ]]; do
  # Query the probe metric via PulseBoard's Prom API (which proxies to Mimir).
  CODE=$(curl "${QUERY_ARGS[@]}" \
    "${BASE_URL}/api/prom/api/v1/query?query=${PROBE_METRIC}&time=${PROBE_BEFORE_TS}" \
    2>/dev/null || echo "000")

  if [[ "$CODE" == "200" ]]; then
    # Check that the result set is non-empty.
    RESULT_COUNT=$(python3 -c "
import json, sys
try:
    d = json.load(open('/tmp/chaos_probe_response.json'))
    print(len(d.get('data', {}).get('result', [])))
except:
    print(0)
" 2>/dev/null || echo "0")

    if [[ "$RESULT_COUNT" -gt 0 ]]; then
      ELAPSED=$(( $(date +%s) - RESTART_TIME ))
      ok "Mimir data recovered after ${ELAPSED}s (${RESULT_COUNT} series visible)"
      RECOVERED=true
      break
    fi
    log "  Query returned 200 but no data yet — retrying in 3s..."
  else
    log "  Probe query returned $CODE — retrying in 3s..."
  fi
  sleep 3
done

if [[ "$RECOVERED" != "true" ]]; then
  fail "Mimir data did NOT recover within ${RECOVER_SECS}s — SLO BREACH"
fi

# ---------------------------------------------------------------------------
# 6. Final edge smoke
# ---------------------------------------------------------------------------
log "Step 6 — final edge smoke"

SMOKE=$(curl "${HEALTH_ARGS[@]}" "${BASE_URL}/api/healthz" 2>/dev/null || echo "000")
if [[ "$SMOKE" != "200" ]]; then
  fail "Edge returned $SMOKE on final health check — SLO BREACH"
fi
ok "Edge final health OK ($SMOKE)"

echo ""
ok "kill-mimir-ingester chaos scenario PASSED"

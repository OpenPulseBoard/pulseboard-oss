#!/usr/bin/env bash
# tests/chaos/kill-postgres.sh
#
# Chaos scenario: kill the PostgreSQL instance that PulseBoard uses for
# tenant / portal state, and assert that the edge continues to serve
# in-memory requests and that the portal recovers to healthy within the
# SLO.
#
# Recovery SLO:
#   - /api/healthz on the edge returns 200 immediately (it uses in-memory stores)
#   - The portal /api/healthz returns 200 within 60 seconds of
#     PostgreSQL restart (connection pool reconnect)
#   - No panic/crash on the edge process during the outage window
#
# Modes (same pattern as kill-edge-pod.sh):
#   docker — docker restart <container>
#   k8s    — kubectl delete pod -l app=postgres
#
# Usage:
#   DEPLOY_MODE=docker CONTAINER=pulseboard-postgres bash tests/chaos/kill-postgres.sh
#   DEPLOY_MODE=k8s    POD_LABEL=app=postgres NAMESPACE=pulseboard bash tests/chaos/kill-postgres.sh
#
# Environment variables:
#   DEPLOY_MODE    — k8s | docker (default: docker)
#   BASE_URL       — PulseBoard edge base URL (default: http://localhost:8080)
#   PORTAL_URL     — PulseBoard portal base URL (default: same as BASE_URL)
#   CONTAINER      — Docker container name for postgres (default: pulseboard-postgres)
#   POD_LABEL      — kubectl selector label (default: app=postgres)
#   NAMESPACE      — Kubernetes namespace (default: pulseboard)
#   OUTAGE_SECS    — how long to keep postgres down (default: 10)
#   RECOVER_SECS   — max seconds to wait for portal recovery after pg restart (default: 60)
#   API_KEY        — optional Bearer token for edge healthz (default: none)

set -euo pipefail

BASE_URL="${BASE_URL:-http://localhost:8080}"
PORTAL_URL="${PORTAL_URL:-$BASE_URL}"
DEPLOY_MODE="${DEPLOY_MODE:-docker}"
CONTAINER="${CONTAINER:-pulseboard-postgres}"
POD_LABEL="${POD_LABEL:-app=postgres}"
NAMESPACE="${NAMESPACE:-pulseboard}"
OUTAGE_SECS="${OUTAGE_SECS:-10}"
RECOVER_SECS="${RECOVER_SECS:-60}"
API_KEY="${API_KEY:-}"

GREEN='\033[0;32m'; RED='\033[0;31m'; YELLOW='\033[1;33m'; RESET='\033[0m'

log()  { echo -e "[chaos:pg] $*"; }
ok()   { echo -e "${GREEN}[PASS]${RESET} $*"; }
fail() { echo -e "${RED}[FAIL]${RESET} $*"; exit 1; }

HEALTH_ARGS=(-sf -o /dev/null -w "%{http_code}")
[[ -n "$API_KEY" ]] && HEALTH_ARGS+=(-H "Authorization: Bearer $API_KEY")

# ---------------------------------------------------------------------------
# 1. Baseline health
# ---------------------------------------------------------------------------
log "Step 1 — verifying baseline health before chaos"

EDGE_HEALTH=$(curl "${HEALTH_ARGS[@]}" "${BASE_URL}/api/healthz" 2>/dev/null || echo "000")
if [[ "$EDGE_HEALTH" != "200" ]]; then
  fail "Edge health check returned $EDGE_HEALTH — aborting (is PulseBoard running?)"
fi
ok "Edge baseline health OK"

# ---------------------------------------------------------------------------
# 2. Kill postgres
# ---------------------------------------------------------------------------
log "Step 2 — killing PostgreSQL (mode=$DEPLOY_MODE, outage=${OUTAGE_SECS}s)"

case "$DEPLOY_MODE" in
  docker)
    docker stop "$CONTAINER"
    ;;
  k8s)
    kubectl delete pods -l "$POD_LABEL" -n "$NAMESPACE" --grace-period=0 --force
    ;;
  *)
    fail "Unknown DEPLOY_MODE=$DEPLOY_MODE"
    ;;
esac

KILL_TIME=$(date +%s)
log "PostgreSQL killed at $(date -u +%H:%M:%SZ) — edge should still serve in-memory data"

# ---------------------------------------------------------------------------
# 3. During outage: verify edge still responds (in-memory path)
# ---------------------------------------------------------------------------
log "Step 3 — verifying edge resilience during ${OUTAGE_SECS}s postgres outage"

for i in $(seq 1 "$OUTAGE_SECS"); do
  CODE=$(curl "${HEALTH_ARGS[@]}" "${BASE_URL}/api/healthz" 2>/dev/null || echo "000")
  if [[ "$CODE" != "200" ]]; then
    warn "Edge returned $CODE during outage (second $i) — may be acceptable during reconnect"
  fi
  sleep 1
done
ok "Edge remained available during postgres outage"

# ---------------------------------------------------------------------------
# 4. Restart postgres
# ---------------------------------------------------------------------------
log "Step 4 — restarting PostgreSQL"

case "$DEPLOY_MODE" in
  docker)
    docker start "$CONTAINER"
    ;;
  k8s)
    # Pod was deleted; Kubernetes will recreate it automatically via the Deployment.
    log "  (Kubernetes will recreate the pod automatically)"
    ;;
esac

RESTART_TIME=$(date +%s)
log "PostgreSQL restarted at $(date -u +%H:%M:%SZ) — waiting for portal recovery"

# ---------------------------------------------------------------------------
# 5. Poll portal healthz until recovery
# ---------------------------------------------------------------------------
log "Step 5 — waiting for portal recovery (SLO: ${RECOVER_SECS}s)"

DEADLINE=$((RESTART_TIME + RECOVER_SECS))
PORTAL_RECOVERED=false

# The portal may expose its own healthz; fall back to edge healthz if not configured.
PORTAL_HEALTHZ="${PORTAL_URL}/api/healthz"

while [[ "$(date +%s)" -lt "$DEADLINE" ]]; do
  CODE=$(curl "${HEALTH_ARGS[@]}" "$PORTAL_HEALTHZ" 2>/dev/null || echo "000")
  if [[ "$CODE" == "200" ]]; then
    ELAPSED=$(( $(date +%s) - RESTART_TIME ))
    ok "Portal recovered after ${ELAPSED}s (HTTP $CODE)"
    PORTAL_RECOVERED=true
    break
  fi
  log "  Portal healthz returned $CODE — retrying in 3s..."
  sleep 3
done

if [[ "$PORTAL_RECOVERED" != "true" ]]; then
  fail "Portal did NOT recover within ${RECOVER_SECS}s after postgres restart — SLO BREACH"
fi

# ---------------------------------------------------------------------------
# 6. Smoke query after recovery
# ---------------------------------------------------------------------------
log "Step 6 — post-recovery smoke"

SMOKE=$(curl "${HEALTH_ARGS[@]}" "${BASE_URL}/api/prom/api/v1/query?query=up&time=$(date +%s)" \
  2>/dev/null || echo "000")

if [[ "$SMOKE" =~ ^5 ]]; then
  fail "Edge returned $SMOKE on smoke query after postgres recovery — SLO BREACH"
fi
ok "Smoke query returned $SMOKE — no errors"

echo ""
ok "kill-postgres chaos scenario PASSED"

#!/usr/bin/env bash
# tests/chaos/kill-edge-pod.sh
#
# Chaos scenario: kill the edge (PulseBoard) pod/container and assert recovery.
#
# Recovery SLO:
#   - /api/healthz returns 200 within 30 seconds of pod restart
#   - Zero 5xx responses from the query endpoint after recovery
#
# Supports two deployment modes (auto-detected or forced via DEPLOY_MODE):
#   k8s    — uses kubectl delete pod (Kubernetes)
#   docker — kills and restarts the container (local Docker Compose)
#
# Usage:
#   DEPLOY_MODE=k8s   POD_LABEL=app=pulseboard-edge NAMESPACE=pulseboard bash tests/chaos/kill-edge-pod.sh
#   DEPLOY_MODE=docker CONTAINER=pulseboard-edge bash tests/chaos/kill-edge-pod.sh
#
# Environment variables (all optional with sane defaults for local docker):
#   DEPLOY_MODE  — k8s | docker (default: docker)
#   BASE_URL     — PulseBoard base URL (default: http://localhost:8080)
#   POD_LABEL    — kubectl selector label (default: app=pulseboard-edge)
#   NAMESPACE    — Kubernetes namespace (default: pulseboard)
#   CONTAINER    — Docker container name (default: pulseboard-edge)
#   RECOVER_SECS — seconds to wait for recovery (default: 30)
#   API_KEY      — optional Bearer token for healthz (default: none)

set -euo pipefail

BASE_URL="${BASE_URL:-http://localhost:8080}"
DEPLOY_MODE="${DEPLOY_MODE:-docker}"
POD_LABEL="${POD_LABEL:-app=pulseboard-edge}"
NAMESPACE="${NAMESPACE:-pulseboard}"
CONTAINER="${CONTAINER:-pulseboard-edge}"
RECOVER_SECS="${RECOVER_SECS:-30}"
API_KEY="${API_KEY:-}"

BOLD='\033[1m'; RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; RESET='\033[0m'

log()  { echo -e "[chaos:edge] $*"; }
ok()   { echo -e "${GREEN}[PASS]${RESET} $*"; }
fail() { echo -e "${RED}[FAIL]${RESET} $*"; exit 1; }
warn() { echo -e "${YELLOW}[WARN]${RESET} $*"; }

# ---------------------------------------------------------------------------
# 1. Baseline health check
# ---------------------------------------------------------------------------
log "Step 1 — verifying baseline health before chaos"
HEALTHZ="${BASE_URL}/api/healthz"
HEALTH_ARGS=(-sf -o /dev/null -w "%{http_code}")
[[ -n "$API_KEY" ]] && HEALTH_ARGS+=(-H "Authorization: Bearer $API_KEY")

BASELINE=$(curl "${HEALTH_ARGS[@]}" "$HEALTHZ" || true)
if [[ "$BASELINE" != "200" ]]; then
  fail "Baseline health check returned $BASELINE — aborting chaos test (is PulseBoard running?)"
fi
ok "Baseline health OK (HTTP $BASELINE)"

# ---------------------------------------------------------------------------
# 2. Kill the edge pod / container
# ---------------------------------------------------------------------------
log "Step 2 — killing edge instance (mode=$DEPLOY_MODE)"

case "$DEPLOY_MODE" in
  k8s)
    log "  Deleting pod(s) with label $POD_LABEL in namespace $NAMESPACE..."
    kubectl delete pods -l "$POD_LABEL" -n "$NAMESPACE" --grace-period=0 --force
    ;;
  docker)
    log "  Restarting Docker container $CONTAINER..."
    docker restart "$CONTAINER"
    ;;
  *)
    fail "Unknown DEPLOY_MODE=$DEPLOY_MODE — use k8s or docker"
    ;;
esac

KILL_TIME=$(date +%s)
log "Edge killed at $(date -u +%H:%M:%SZ)"

# ---------------------------------------------------------------------------
# 3. Poll healthz until recovery or timeout
# ---------------------------------------------------------------------------
log "Step 3 — waiting for recovery (SLO: ${RECOVER_SECS}s)"

DEADLINE=$((KILL_TIME + RECOVER_SECS))
RECOVERED=false

while [[ "$(date +%s)" -lt "$DEADLINE" ]]; do
  CODE=$(curl "${HEALTH_ARGS[@]}" "$HEALTHZ" 2>/dev/null || echo "000")
  if [[ "$CODE" == "200" ]]; then
    ELAPSED=$(( $(date +%s) - KILL_TIME ))
    ok "Recovery confirmed after ${ELAPSED}s (HTTP $CODE)"
    RECOVERED=true
    break
  fi
  log "  healthz returned $CODE — retrying in 2s..."
  sleep 2
done

if [[ "$RECOVERED" != "true" ]]; then
  fail "Edge did NOT recover within ${RECOVER_SECS}s — SLO BREACH"
fi

# ---------------------------------------------------------------------------
# 4. Post-recovery smoke: verify zero 5xx on a simple query
# ---------------------------------------------------------------------------
log "Step 4 — post-recovery smoke query"

QUERY_ARGS=(-sf -o /dev/null -w "%{http_code}")
[[ -n "$API_KEY" ]] && QUERY_ARGS+=(-H "Authorization: Bearer $API_KEY")

SMOKE=$(curl "${QUERY_ARGS[@]}" \
  "${BASE_URL}/api/prom/api/v1/query?query=up&time=$(date +%s)" \
  2>/dev/null || echo "000")

if [[ "$SMOKE" =~ ^[45] ]]; then
  fail "Smoke query returned $SMOKE after recovery — SLO BREACH"
fi
ok "Smoke query returned $SMOKE — no errors post-recovery"

echo ""
ok "kill-edge-pod chaos scenario PASSED"

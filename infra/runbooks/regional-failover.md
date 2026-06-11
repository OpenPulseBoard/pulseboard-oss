# Runbook: Regional failover

**Scope:** Promote the warm standby region to primary when the active
region is degraded or unreachable. Targets **RPO ≤ 60 s, RTO ≤ 15 min.**

See  for the topology this runbook
operates against.

---

## When to invoke

- Active-region NLB health check has been red for ≥ 5 min and on-call
  has confirmed the outage is not a monitoring artifact, **or**
- a cloud-provider regional advisory explicitly names the active
  region, **or**
- a tabletop / GameDay drill (planned exercise).

Do **not** invoke for single-AZ failures — those are absorbed
automatically by topology spread (see Phase 6 #1).

## Pre-flight (≤ 2 min)

1. Confirm the standby region's edge deployment is healthy:
   `kubectl --context=$STANDBY get pods -n pulseboard -l app=edge`.
2. Confirm storage replication lag:
   - Aurora Global DB lag < 5 s (`SELECT * FROM aurora_global_db_status();`).
   - S3 CRR backlog == 0 (CloudWatch `ReplicationLatency` metric).
   - Mimir/Loki/Tempo store-gateways in standby can list recent blocks.
3. Page the secondary on-call and open an incident channel.

## Promotion sequence (≤ 10 min)

Execute **in order**; do not parallelize.

1. **Freeze writes to the active region** (best-effort if it is
   reachable):
   - Scale edge deployment to 0: `kubectl --context=$ACTIVE scale deploy/edge --replicas=0 -n pulseboard`.
2. **Promote Postgres.**
   - Aurora Global: `aws rds failover-global-cluster --global-cluster-identifier pulseboard-global --target-db-cluster-identifier $STANDBY_CLUSTER_ARN`.
   - Self-hosted Patroni: `patronictl -c /etc/patroni.yml failover --candidate $STANDBY_NODE`.
   - Wait for `pg_is_in_recovery() = false` on the new primary.
3. **Promote object-store-backed stores.** No action required for
   Mimir/Loki/Tempo themselves — they read whichever bucket the
   standby deployment is pointed at. Verify each component's
   `/ready` returns 200 in the standby cluster.
4. **Scale the standby edge tier.** `kubectl --context=$STANDBY scale deploy/edge --replicas=6 -n pulseboard` and wait for `readinessProbe` to pass on all pods.
5. **Flip GeoDNS.** Update the Route 53 / Cloudflare weighted record
   for `*.pulseboard.app` to send 100 % to the standby NLB. TTL is
   30 s.
6. **Smoke test.**
   - `curl -sS https://api.pulseboard.app/api/healthz` returns 200.
   - Issue a metric via `/ingest/metrics` with a synthetic tenant key
     and query it back via `/api/prom/api/v1/query`.
   - Confirm the `__meta__` tenant's `pulse-self` dashboard is
     rendering recent data in the standby region.

## Post-failover (within 24 h)

- Demote the former active region to standby once it is reachable;
  do **not** flip GeoDNS back automatically.
- Rotate the cluster KEK if there is any reason to believe the old
  region's secret material is compromised.
- File the incident report; tag with `region-failover`.
- Schedule the post-mortem within 5 business days.

## Failback

Failback is **manual and planned** — never urgent. Same sequence with
the roles reversed, executed during a low-traffic window with a 24 h
heads-up to enterprise customers.

## Drill cadence

- Tabletop: quarterly.
- Live failover in staging: every 6 months.
- Live failover in prod (announced maintenance window): annually.

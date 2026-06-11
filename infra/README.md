# infra

Infrastructure-as-code, runtime configs, and operator runbooks for
self-hosted PulseBoard deployments.

Current layout:

```
infra/
├── mimir/          # Mimir runtime config used by docker-compose and Helm
├── pulseagent/     # Sidecar agent config + entrypoint wrapper baked into the workspace image
└── runbooks/       # Operator runbooks: failover, rotation, portal & billing
```

## Runbooks

The deployment architecture for HA topology and TLS is documented in
[../docs/DEPLOYMENT.md](../docs/DEPLOYMENT.md). Operator runbooks live
here:

- [`runbooks/regional-failover.md`](runbooks/regional-failover.md) —
  promoting the warm standby region when the active region degrades.
- [`runbooks/tls-rotation.md`](runbooks/tls-rotation.md) — routine
  leaf renewal, intermediate / root rotation, and emergency
  compromise response.
- [`runbooks/portal-and-billing.md`](runbooks/portal-and-billing.md) —
  customer portal, Stripe webhook setup, free-tier idle sleeper, and
  per-customer tear-down.

Helm charts, Terraform modules, and Dockerfiles will land in the
folders above as Phase 6 #1/#2 move from designed to implemented.

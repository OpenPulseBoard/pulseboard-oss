# infra

Infrastructure-as-code for PulseBoard Cloud.

Planned layout (see [PLAN.md](../PLAN.md) phases 3, 6, 7):

```
infra/
├── docker/         # Dockerfiles for edge, control-plane, ui
├── helm/           # Helm charts for edge, storage adapters, control-plane
├── terraform/
│   ├── modules/    # reusable modules (vpc, postgres, mimir, loki, tempo, …)
│   └── envs/
│       ├── dev/
│       ├── staging/
│       └── prod/
└── runbooks/       # operator runbooks: failover, rotation, restore
```

Nothing here yet — track progress in PLAN.md phase 6 (Reliability,
security, compliance).

## Runbooks (Phase 6 #1 + #2)

The deployment architecture for HA topology and TLS is documented in
[PLAN.md → Phase 6](../PLAN.md). Operator runbooks live here:

- [`runbooks/regional-failover.md`](runbooks/regional-failover.md) —
  promoting the warm standby region when the active region degrades.
- [`runbooks/tls-rotation.md`](runbooks/tls-rotation.md) — routine
  leaf renewal, intermediate / root rotation, and emergency
  compromise response.
- [`runbooks/portal-and-billing.md`](runbooks/portal-and-billing.md) —
  Phase 10 customer portal, Stripe webhook setup, free-tier idle
  sleeper, and per-customer tear-down.

Helm charts, Terraform modules, and Dockerfiles will land in the
folders above as Phase 6 #1/#2 move from designed to implemented.

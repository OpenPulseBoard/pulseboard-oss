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

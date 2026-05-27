# docs

Source for the public PulseBoard docs site.

Planned layout:

```
docs/
├── src/                # MkDocs / Docusaurus sources
│   ├── getting-started/
│   ├── guides/
│   │   ├── ingest-otlp.md
│   │   ├── ingest-prometheus.md
│   │   ├── alerting.md
│   │   └── billing.md
│   ├── reference/      # auto-generated from OpenAPI
│   └── concepts/
├── mkdocs.yml          # or docusaurus.config.js
└── README.md
```

Nothing here yet — track progress in PLAN.md phase 7 (Commercial surface).

Current hand-maintained docs in this folder:

- [`DEPLOYMENT.md`](DEPLOYMENT.md) — operator deployment guide
- [`CONTRACT.md`](CONTRACT.md) — cloud/workspace split contract used for the repo split
- [`REPO_SPLIT.md`](REPO_SPLIT.md) — exact extraction and cleanup commands for the two-repo split

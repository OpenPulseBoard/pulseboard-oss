# src/edge

The PulseBoard **edge service**: stateless F#/Suave HTTP listener that
authenticates, validates, applies quotas, and forwards telemetry to the
storage tier.

The `.fs` sources, `PulseBoard.fsproj`, and `wwwroot/` now live in this
directory. Suave is pulled from NuGet (`Suave` 3.4.0) via
`PackageReference`; the old in-tree Suave `ProjectReference` and Paket
setup are gone. Build and run from the repo root with:

```bash
dotnet build src/edge/PulseBoard.fsproj
dotnet run --project src/edge -- --port=8775 --data=./pulse-data
```

See the feature guides under [../../docs/](../../docs/) for the OTLP
receiver, Prometheus `remote_write`, Loki push, tenant context, and
quota middleware that live here.

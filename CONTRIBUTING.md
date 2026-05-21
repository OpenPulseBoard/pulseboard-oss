# Contributing to PulseBoard

Thanks for considering a contribution! PulseBoard is pre-alpha — APIs and
internals can change without notice. The roadmap is in [PLAN.md](PLAN.md);
issues and PRs that move the project along that plan are the easiest to
get merged.

## Ground rules

- Be excellent to each other. See [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
- By submitting a PR you certify that your contribution complies with the
  [Developer Certificate of Origin](https://developercertificate.org/)
  and is offered under the project's [AGPL-3.0-or-later](LICENSE) license.
- For non-trivial changes, please open an issue first so we can align on
  approach before you spend time writing code.

## Dev loop

```bash
# Build
dotnet build

# Run the edge app
dotnet run --project src/edge -- --port=8775 --data=./pulse-data

# Smoke test
curl -X POST -H 'content-type: application/json' \
     -d '{"name":"cpu","value":0.42}' \
     http://127.0.0.1:8775/ingest/metrics
```

Tests aren't wired up yet. When you add a feature, please add a test
project alongside it (`*.Tests.fsproj`) — CI auto-discovers them.

## Code style

- F# code follows the existing modules' style: short comments explain
  *why*, not *what*; no doc-comment ceremony on internal helpers.
- One module per file; file order in `.fsproj` is the dependency order.
- Public surface that crosses the edge/control-plane boundary should be
  expressible in terms of `WebPart` composition.

## Security

Please do **not** open public issues for security vulnerabilities. See
[SECURITY.md](SECURITY.md) for the private disclosure path.

## Sign-off

We use the [DCO](https://developercertificate.org/). Add a `Signed-off-by`
trailer to your commits:

```bash
git commit -s -m "edge: validate Prom remote_write timestamps"
```

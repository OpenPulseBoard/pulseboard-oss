.PHONY: build test test-fast test-postgres coverage bench bench-k6 chaos format install-hooks

# Build all projects
build:
	dotnet build PulseBoard.sln

# Run unit + property tests and collect Cobertura coverage
test:
	dotnet test tests/edge/PulseBoard.Tests.fsproj \
	  --configuration Release \
	  --filter "Category!=Postgres&Category!=Integration" \
	  --collect:"XPlat Code Coverage" \
	  --results-directory coverage/ \
	  -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura

# Fast subset only — suitable for pre-commit hooks
test-fast:
	dotnet test tests/edge/PulseBoard.Tests.fsproj \
	  --filter "Category=Fast" \
	  --no-build

# Run Postgres-backed integration tests (requires Docker)
test-postgres:
	dotnet test tests/edge/PulseBoard.Tests.fsproj \
	  --filter "Category=Postgres" \
	  --configuration Release

# Generate HTML coverage report + run the gate script locally
# Requires: dotnet tool install -g dotnet-reportgenerator-globaltool
coverage: test
	reportgenerator \
	  -reports:"coverage/**/coverage.cobertura.xml" \
	  -targetdir:"coverage-report" \
	  -reporttypes:"Html;TextSummary"
	@cat coverage-report/Summary.txt
	@python3 tests/coverage-gate.py

# Bench suite — Phase 11.5
# Runs all BenchmarkDotNet benchmarks in Release mode and emits JSON artefacts.
# Usage:  make bench
#         make bench FILTER="*Ingest*"   (run a subset)
bench:
	@mkdir -p bench-results
	dotnet run --project tests/bench/PulseBoard.Bench.fsproj \
	  --configuration Release -- \
	  --filter "$(or $(FILTER),*)" \
	  --exporters Json MarkdownExporter \
	  --artifacts bench-results/ \
	  --memoryDiagnoser

# k6 load profile — 10k series × ~1k samples/s for 10 minutes.
# Requires k6 ≥ 0.45 (https://k6.io/docs/getting-started/installation/)
# Usage:  make bench-k6
#         make bench-k6 BASE_URL=https://staging.pulseboard.io API_KEY=pk_xxx
bench-k6:
	@mkdir -p bench-results
	k6 run tests/chaos/k6-load.js \
	  --env BASE_URL=$(or $(BASE_URL),http://localhost:8080) \
	  $(if $(API_KEY),--env API_KEY=$(API_KEY),) \
	  --env DURATION=$(or $(DURATION),10m) \
	  --env VUS_INGEST=$(or $(VUS_INGEST),50) \
	  --env VUS_QUERY=$(or $(VUS_QUERY),10) \
	  --env SERIES=$(or $(SERIES),10000)

# Chaos suite — Phase 11.5
# Runs kill-edge-pod, kill-postgres, and kill-mimir-ingester smoke scenarios.
# Requires a running PulseBoard instance (DEPLOY_MODE=docker by default).
# Usage:  make chaos
#         make chaos DEPLOY_MODE=k8s BASE_URL=https://staging.pulseboard.io
chaos:
	@echo "Running PulseBoard chaos suite (DEPLOY_MODE=$(or $(DEPLOY_MODE),docker))..."
	DEPLOY_MODE=$(or $(DEPLOY_MODE),docker) \
	  BASE_URL=$(or $(BASE_URL),http://localhost:8080) \
	  $(if $(API_KEY),API_KEY=$(API_KEY),) \
	  bash tests/chaos/kill-edge-pod.sh
	DEPLOY_MODE=$(or $(DEPLOY_MODE),docker) \
	  BASE_URL=$(or $(BASE_URL),http://localhost:8080) \
	  $(if $(API_KEY),API_KEY=$(API_KEY),) \
	  bash tests/chaos/kill-postgres.sh
	DEPLOY_MODE=$(or $(DEPLOY_MODE),docker) \
	  BASE_URL=$(or $(BASE_URL),http://localhost:8080) \
	  $(if $(API_KEY),API_KEY=$(API_KEY),) \
	  bash tests/chaos/kill-mimir-ingester.sh
	@echo "All chaos scenarios passed."

# Format all F# source files
format:
	dotnet format PulseBoard.slnx

# Install git hooks — run once per clone
# Sets core.hooksPath so git picks up .githooks/pre-commit automatically.
install-hooks:
	git config core.hooksPath .githooks
	chmod +x .githooks/pre-commit
	@echo "Git hooks installed. pre-commit: dotnet format + Category=Fast tests."

.PHONY: build test test-fast test-postgres coverage bench chaos format

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

# Bench suite — Phase 11.5 (not yet implemented)
bench:
	@echo "Bench suite lives in tests/bench/ — not yet implemented (Phase 11.5)"

# Chaos suite — Phase 11.5 (not yet implemented)
chaos:
	@echo "Chaos suite lives in tests/chaos/ — not yet implemented (Phase 11.5)"

# Format all F# source files
format:
	dotnet format PulseBoard.sln

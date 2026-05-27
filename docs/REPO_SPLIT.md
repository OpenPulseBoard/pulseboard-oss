# Repository Split Runbook

This runbook turns the current in-repo workspace/cloud separation into
two repositories:

- public OSS workspace repo
- private `pulseboard-cloud` repo

It assumes the current repository root is the working tree for the final
split branch.

## Preconditions

Before running the split:

1. `dotnet build src/edge/PulseBoard.fsproj`
2. `dotnet build src/cloud/PulseBoard.Cloud.fsproj`
3. Confirm the contract in [`CONTRACT.md`](CONTRACT.md) is the intended
   cross-repo boundary.
4. Create the destination GitHub repositories:
   - public OSS repo
   - private `pulseboard-cloud` repo
5. Ensure `git filter-repo` is installed.

Example install on macOS:

```bash
brew install git-filter-repo
```

## What moves to the cloud repo

History-preserved extraction paths:

- `src/cloud/`
- `infra/cloud/`
- `infra/runbooks/portal-and-billing.md`
- `cloud.Dockerfile`

These items should be copied or recreated in the cloud repo after the
history-preserving extraction because they are currently shared or mixed
with OSS concerns:

- the cloud-only portion of `.github/workflows/image.yml`
- hosted sections currently embedded in `docs/DEPLOYMENT.md`
- a copy of `docs/CONTRACT.md` if you want the contract document to live
  in both repos

## Step 1: Create the private cloud repo from history

Work from a fresh clone so `git filter-repo` can rewrite history without
touching your main working copy.

```bash
cd /tmp
git clone --no-local /Users/ademar/work/PulseBoard pulseboard-cloud
cd pulseboard-cloud
git filter-repo --force \
  --path src/cloud/ \
  --path infra/cloud/ \
  --path infra/runbooks/portal-and-billing.md \
  --path cloud.Dockerfile
```

Then connect the new private remote:

```bash
git remote rename origin monorepo-origin
git remote add origin git@github.com:<org>/pulseboard-cloud.git
git branch -M main
```

## Step 2: Normalize the extracted cloud repo

After extraction, make these cloud-repo-only adjustments:

1. Add a cloud-only workflow that builds and publishes only
   `registry.fly.io/pulseboard-cloud`.
2. Add a cloud-owned deployment guide by copying the hosted sections out
   of the current `docs/DEPLOYMENT.md`.
3. Add or copy `docs/CONTRACT.md` so the cloud repo carries the
   workspace/cloud interface document.
4. Verify no `src/edge/` source or OSS-only deployment instructions
   remain.

Suggested verification commands in the extracted repo:

```bash
dotnet build src/cloud/PulseBoard.Cloud.fsproj
rg "src/edge|PulseBoard.fsproj|registry.fly.io/pulseboard1" -n .
```

Expected result:

- `src/edge` source should not exist
- references to `registry.fly.io/pulseboard1` should remain only where
  the cloud side intentionally consumes the OSS workspace image

## Step 3: Prepare the OSS repo cleanup branch

Back in the main working repo:

```bash
cd /Users/ademar/work/PulseBoard
git checkout -b split/oss-public
```

Remove cloud-owned paths:

```bash
git rm -r src/cloud infra/cloud
git rm cloud.Dockerfile
git rm infra/runbooks/portal-and-billing.md
```

Then update OSS-facing docs and workflows:

1. Trim `.github/workflows/image.yml` so it validates and publishes only
   the workspace image `registry.fly.io/pulseboard1`.
2. Remove hosted deployment details from `docs/DEPLOYMENT.md` and keep
   only self-host / OSS guidance.
3. Update `README.md` to describe the OSS product only and mention that
   hosted control-plane code lives in a separate private repo.
4. Remove cloud-repo references that imply same-repo source access.

Suggested verification commands in the OSS repo:

```bash
dotnet build src/edge/PulseBoard.fsproj
rg "src/cloud|cloud.Dockerfile|pulseboard-cloud" -n README.md docs .github src infra
```

Expected result:

- no `src/cloud/` source remains
- no cloud image build logic remains in OSS CI
- OSS docs point hosted operators to the private repo rather than to
  in-tree cloud code

## Step 4: Push both repos

Push the extracted cloud repo first:

```bash
cd /tmp/pulseboard-cloud
git push -u origin main
```

Then push the OSS cleanup branch or new OSS remote:

```bash
cd /Users/ademar/work/PulseBoard
git remote add oss-origin git@github.com:<org>/pulseboard.git
git push -u oss-origin split/oss-public:main
```

## Step 5: Post-split verification

Run these checks before announcing the split:

### OSS repo

```bash
dotnet build src/edge/PulseBoard.fsproj
```

### Cloud repo

```bash
dotnet build src/cloud/PulseBoard.Cloud.fsproj
```

### Artifact boundary

Verify the cloud repo still consumes the workspace image only as an
artifact:

```bash
rg "PULSE_WORKSPACE_IMAGE|registry.fly.io/pulseboard1" -n /tmp/pulseboard-cloud
```

### Functional smoke

1. Start the provisioner in dry-run mode.
2. Start site-only against that provisioner.
3. Verify the bootstrap contract still works via HTTP.

Example:

```bash
dotnet run --project src/cloud/PulseBoard.Cloud.fsproj -- --mode=provisioner --dry-run --port=19001
dotnet run --project src/cloud/PulseBoard.Cloud.fsproj -- --site-only --port=19002 --provisioner-url=http://127.0.0.1:19001
curl -X POST http://127.0.0.1:19002/api/signup \
  -H 'content-type: application/json' \
  -d '{"slug":"acme","email":"alice@acme.co"}'
```

## Step 6: Immediate follow-up after the split

1. Replace the temporary combined image workflow with one workflow per
   repo.
2. Pin the cloud repo to a tested workspace image tag instead of
   `:latest`.
3. Add a short compatibility note in the cloud repo release notes:
   cloud release -> minimum supported `pulseboard1` image tag.
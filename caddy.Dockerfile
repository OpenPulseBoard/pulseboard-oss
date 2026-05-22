# syntax=docker/dockerfile:1.6
#
# Image for the pulseboard-caddy Fly app: stock Caddy with our
# Caddyfile baked in. Build context is the repo root so the COPY can
# reach infra/cloud/Caddyfile without a sibling copy.
#
# Build / deploy (run from repo root):
#   fly deploy -a pulseboard-caddy \
#       --config infra/cloud/fly/caddy.toml \
#       --dockerfile caddy.Dockerfile

FROM caddy:2-alpine
COPY infra/cloud/Caddyfile /etc/caddy/Caddyfile

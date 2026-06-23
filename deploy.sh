#!/bin/bash
# Deploy AFH Sync on the box. Builds ONLY the services whose sources changed in
# the pull (override by passing service names, e.g. `./deploy.sh worker`).
# The heavy PowerShell/ExchangeOnlineManagement layer lives in afh-runtime-base
# and is built once — see base/Dockerfile.
#
# TIP: always run inside tmux so a dropped SSH can't abandon a half-build.
cd "$(dirname "$0")"

BASE_IMAGE="afh-runtime-base:1"

build_base() {
  echo "Building $BASE_IMAGE (heavy: PowerShell + ExchangeOnlineManagement, several minutes)…"
  docker build -t "$BASE_IMAGE" base/ || { echo "✗ base image build failed"; exit 1; }
  echo "✓ $BASE_IMAGE built"
}

echo "━━━ 0. Ensure shared runtime base image ━━━"
if docker image inspect "$BASE_IMAGE" >/dev/null 2>&1; then
  echo "✓ $BASE_IMAGE present — skipping the heavy PowerShell layer"
else
  echo "$BASE_IMAGE missing — building it once now."
  build_base
fi

echo ""
echo "━━━ 1. Pull latest code ━━━"
BEFORE=$(git rev-parse HEAD)
git pull --ff-only || { echo "git pull failed"; exit 1; }
AFTER=$(git rev-parse HEAD)
echo "HEAD: $(git log --oneline -1)"

echo ""
echo "━━━ 2. Select services to build ━━━"
if [ "$#" -gt 0 ]; then
  SVCS="$*"
  echo "Services from args: $SVCS"
else
  CHANGED=$(git diff --name-only "$BEFORE" "$AFTER")
  # base/ or compose changes are infra-wide → rebuild base + everything.
  if echo "$CHANGED" | grep -qE '^base/'; then
    echo "base/ changed — rebuilding runtime base."
    build_base
  fi
  SVCS=""
  # worker image links api + shared sources, so any of them rebuilds worker.
  if echo "$CHANGED" | grep -qE '^(worker|api|shared)/'; then SVCS="$SVCS worker"; fi
  if echo "$CHANGED" | grep -qE '^(api|shared)/';        then SVCS="$SVCS api";    fi
  if echo "$CHANGED" | grep -qE '^frontend/';            then SVCS="$SVCS frontend"; fi
  if echo "$CHANGED" | grep -qE '^(compose\.yaml|base/)'; then SVCS="worker api frontend"; fi
  SVCS=$(echo "$SVCS" | xargs)
  if [ -z "$SVCS" ]; then
    echo "No service-relevant changes — rolling existing images forward only."
  else
    echo "Building: $SVCS"
  fi
fi

echo ""
echo "━━━ 3. Build selected services ━━━"
for svc in $SVCS; do
  echo "── build: $svc ──"
  if docker compose build "$svc"; then
    echo "✓ $svc image built"
  elif [ "$svc" = "worker" ]; then
    echo "✗ worker build failed — aborting (worker is the critical path)"
    exit 1
  else
    echo "⚠ $svc build failed — keeping previous $svc image"
  fi
done

echo ""
echo "━━━ 4. Roll services forward ━━━"
# --force-recreate not needed: compose picks up rebuilt images; unchanged ones are left alone.
docker compose up -d --remove-orphans

echo ""
echo "━━━ 5. Health check ━━━"
sleep 3
docker ps --format "table {{.Names}}\t{{.Status}}" | grep afh-

echo ""
echo "━━━ 6. Worker tail ━━━"
docker logs afh-worker --tail 15 2>&1 | tail -15

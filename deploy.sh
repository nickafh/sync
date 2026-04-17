#!/bin/bash
cd "$(dirname "$0")"

echo "━━━ 1. Pull latest code ━━━"
git pull --ff-only || { echo "git pull failed"; exit 1; }
echo "HEAD: $(git log --oneline -1)"

echo ""
echo "━━━ 2. Build worker (critical path) ━━━"
if docker compose build worker; then
  echo "✓ worker image built"
else
  echo "✗ worker build failed — aborting"
  exit 1
fi

echo ""
echo "━━━ 3. Build frontend ━━━"
docker compose build frontend || echo "⚠ frontend build failed — continuing"

echo ""
echo "━━━ 4. Build api (may fail on ubuntu archive outages, non-fatal) ━━━"
docker compose build api || echo "⚠ api build failed — keeping previous api image"

echo ""
echo "━━━ 5. Roll services forward ━━━"
# --force-recreate picks up new images; skipping missing ones gracefully.
# Services with unchanged images are left alone.
docker compose up -d --remove-orphans

echo ""
echo "━━━ 6. Health check ━━━"
sleep 3
docker ps --format "table {{.Names}}\t{{.Status}}" | grep afh-

echo ""
echo "━━━ 7. Worker tail ━━━"
docker logs afh-worker --tail 15 2>&1 | tail -15

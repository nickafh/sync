#!/bin/bash
set -e
cd "$(dirname "$0")"
git pull
docker compose build --no-cache api worker frontend
docker compose up -d --force-recreate api worker frontend
echo "--- API startup logs ---"
sleep 3
docker logs afh-api --tail 20

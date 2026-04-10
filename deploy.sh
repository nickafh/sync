#!/bin/bash
set -e
cd "$(dirname "$0")"
git pull
docker compose up -d --build api worker
docker logs afh-api --tail 20

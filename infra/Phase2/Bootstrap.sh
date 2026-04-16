#!/usr/bin/env bash
# Phase 2 local infrastructure bootstrap.
# Provisions bucket, topics, cache namespace after `docker compose up -d`.
set -euo pipefail

MC=${MC:-mc}
RABBIT_CLI=${RABBIT_CLI:-rabbitmqadmin}
REDIS_CLI=${REDIS_CLI:-redis-cli}

echo "[Phase 2] Provisioning MinIO bucket: muallimi-ai-tutor-local"
$MC alias set muallimi-local http://localhost:9000 muallimi muallimi_local >/dev/null 2>&1 || true
$MC mb -p muallimi-local/muallimi-ai-tutor-local >/dev/null 2>&1 || true

echo "[Phase 2] Declaring RabbitMQ topics: ai.tutor.request.recorded, ai.tutor.redteam.run.completed"
$RABBIT_CLI -H localhost -u muallimi -p muallimi_local declare exchange name=ai.tutor.request.recorded type=topic durable=true || true
$RABBIT_CLI -H localhost -u muallimi -p muallimi_local declare exchange name=ai.tutor.redteam.run.completed type=topic durable=true || true

echo "[Phase 2] Reserving Redis namespace: ai-tutor:*"
$REDIS_CLI -h localhost -p 6379 SET ai-tutor:bootstrap "phase2-ready" EX 60 || true

echo "[Phase 2] Bootstrap complete."

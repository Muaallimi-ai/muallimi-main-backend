# Phase 6 Rollback Procedures

This document (T086) describes the rollback procedures for each deployable
unit. Each procedure covers: (a) the command sequence; (b) database
migration rollback compatibility; (c) queue-message backward-compatibility
window; (d) data-integrity verification steps; and (e) the maximum rollback
window supported by the service.

> **Constitutional rule**: every rollback runbook must be runnable in local
> parity (Docker Compose) before being trusted in production. The
> `phase6-rollback-smoke.sh` smoke suite (under
> `muallimi-main-backend/infra/`) exercises each of these procedures against
> the local stack.

## 1. muallimi-main-backend

### Command sequence

```bash
# 1. Snapshot current head for evidence
git -C ../muallimi-main-backend rev-parse HEAD > _rollback/main-backend.head

# 2. Deploy previous stable tag (N-1)
helm upgrade muallimi-main-backend charts/main-backend \
  --set image.tag=$PREVIOUS_STABLE_TAG \
  --reuse-values

# 3. Run EF migration down to the previous model version
dotnet ef database update <PreviousMigrationName> \
  --project src/Muallimi.Infrastructure \
  --startup-project src/Muallimi.Api

# 4. Drain in-flight queues (RabbitMQ / ASB) before flipping traffic
./infra/scripts/drain-queues.sh main-backend

# 5. Restore traffic (readiness probe must return 200 for 60 s)
kubectl rollout status deployment/muallimi-main-backend --timeout=5m
```

### Migration rollback compatibility

- Additive-only migrations (Phase 6 MVP) are safe to rollback without data
  loss. Example: new billing columns remain nullable by contract.
- **Breaking migrations require a two-step deploy** (expand → contract).
  Never roll back a contract step; instead roll forward with a hotfix.
- Phase 6 EF migrations are named `Phase6_*` and all tables ship with the
  `additive_only` tag in migration metadata.

### Queue backward compatibility window

- `phase4.downstream.events`, `phase5.downstream.events`, and
  `phase6.operational_events` are additive-only (enforced by
  `AdditiveOnly` contract tests).
- Consumer window: **90 days**. If a consumer has not been upgraded in that
  window, it may see new optional fields but never loses required ones.

### Data integrity verification post-rollback

1. `dotnet test tests/Muallimi.Api.Tests` — all integration tests pass.
2. Verify every active subscription has `current_period_end >= now` and
   `status ∈ {trial, active, grace}`.
3. Confirm `AuditEntry` retention — no rows older than the rolled-back
   schema version should have been deleted (audit rows are append-only).
4. Run `phase6-smoke.sh` and diff evidence against pre-rollback baseline.

### Maximum rollback window

- **72 hours.** Subscriptions created within that window retain all required
  fields in the prior schema. Beyond 72 hours, a rollback requires a
  data-migration fixture for any new required columns.

---

## 2. muallimi-ai-service

### Command sequence

```bash
# 1. Snapshot and redeploy
helm upgrade muallimi-ai-service charts/ai-service \
  --set image.tag=$PREVIOUS_STABLE_TAG --reuse-values

# 2. Rollback prompt registry changes (if any)
curl -X POST $MAIN_BACKEND_URL/api/v1/operator/prompts/rollback \
  -H 'X-Actor-Role: operator' \
  -d '{ "to_version": "<previous>" }'

# 3. Ensure provider adapter bindings point at the compatible models
kubectl exec deploy/muallimi-ai-service -- \
  ./Muallimi.AiService.Cli provider-bindings --verify
```

### Migration rollback compatibility

- `ai_service.pgvector` schema is additive; embedding dimensions must not
  shrink. If rolling back a dimension change, preserve the previous
  collection and route writes there before removing the new collection.

### Queue backward compatibility window

- The ai-service consumes no queues directly; it exposes HTTP (SSE for
  tutor chat, POST for answer generation). Request/response contracts are
  versioned under `/api/v1/...` and additive-only.

### Data integrity verification post-rollback

1. `dotnet test tests/Muallimi.AiService.Tests` (119 tests) — all green.
2. Smoke-test tutor answer endpoint with a canonical question and verify
   guardrail pass + non-refusal response.
3. Confirm `AiRequestRecord` rows continue to flow into the main-backend.

### Maximum rollback window

- **48 hours.** Beyond that, pgvector embeddings may drift with curriculum
  updates; full re-embedding is required before rollback.

---

## 3. muallimi-document-ingestion

### Command sequence

```bash
# 1. Pause the worker
kubectl scale deploy/muallimi-document-ingestion --replicas=0

# 2. Let in-flight messages finish (wait for DLQ depth to stabilize)
./infra/scripts/wait-for-dlq-drain.sh

# 3. Deploy previous stable worker
helm upgrade muallimi-document-ingestion charts/document-ingestion \
  --set image.tag=$PREVIOUS_STABLE_TAG --reuse-values

# 4. Resume with replicas matching pre-rollback
kubectl scale deploy/muallimi-document-ingestion --replicas=$PREV_REPLICAS
```

### Migration rollback compatibility

- The ingestion worker is a pure producer; it writes to the main-backend
  database via HTTP (`/internal/ingestion/results`). No EF migrations here.

### Queue backward compatibility window

- `ingestion.jobs`, `ingestion.results` use additive message schemas.
  Consumer window: **30 days**.

### Data integrity verification post-rollback

1. DLQ depth metric (`ingestion_dead_letter_depth`) returns to zero within
   30 minutes.
2. `ingestion_processed_total` counter resumes incrementing.
3. Spot-check: re-run a canonical PDF and confirm chunks + embeddings land
   in the main-backend.

### Maximum rollback window

- **30 days.** Beyond that, queue messages may reference chunks that no
  longer exist.

---

## 4. Muaallimi-Platform (frontend)

### Command sequence

```bash
# Static/server rollback (Vercel / custom deploy)
vercel promote $PREVIOUS_DEPLOYMENT_URL --scope muaallimi

# Or container-based:
helm upgrade muaallimi-platform charts/frontend \
  --set image.tag=$PREVIOUS_STABLE_TAG --reuse-values
kubectl rollout status deployment/muaallimi-platform --timeout=5m
```

### Migration rollback compatibility

- The frontend is stateless; no migrations. Ensure the backend API version
  it talks to is ≥ the version the rolled-back frontend expects. The
  `NEXT_PUBLIC_BACKEND_URL` should point to a compatible main-backend
  release.

### Queue / API backward compatibility window

- Frontend depends on additive-only API contracts. New fields introduced
  after the rollback target version will be ignored; removed fields must
  not exist yet.

### Data integrity verification post-rollback

1. `npx tsc --noEmit` passes in `Muaallimi-Platform/`.
2. Playwright smoke suite (`tests/e2e/smoke.spec.ts`) passes.
3. Axe WCAG 2.1 AA aggregate report shows zero regressions against the
   baseline.

### Maximum rollback window

- **14 days.** Longer windows risk stale translations or outdated Cairo
  font subsets shipped with the page.

---

## Cross-cutting verification checklist

After any rollback across any unit:

- [ ] Readiness probes (`/health/ready`) return 200 for all four services
      continuously for 5 minutes.
- [ ] `HealthCheckAlertService` has fired zero new alerts since rollback.
- [ ] `AuditEntry` table has a `rollback_performed` action-type row with
      actor = operator, correlation_id set, payload with source/target tags.
- [ ] An incident with severity ≥ `high` is open and in status
      `mitigated` or `resolved`, with `rollback_performed` in its timeline.
- [ ] `phase6-smoke.sh` evidence artifacts match the pre-rollback baseline
      (diff under `_evidence/phase6/rollback-<ts>/`).

## Runbook references

| Runbook | Path |
|---------|------|
| Generic rollback | `runbooks/rollback-generic.md` |
| Billing rollback | `runbooks/billing-rollback.md` |
| AI-service rollback | `runbooks/ai-service-rollback.md` |
| Ingestion DLQ drain | `runbooks/ingestion-dlq-drain.md` |

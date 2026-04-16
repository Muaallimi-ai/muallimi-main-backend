# Contract: Curriculum Admin API

## Purpose

HTTP contract served by `muallimi-main-backend` and consumed by the
`Muaallimi-Platform` Curriculum Admin Portal. Covers upload, ingestion tracking,
structure review, generation control, tiered review, coverage, and invalidation.

## Catalogue Record

```yaml
contract_id: curriculum.admin.api
contract_type: service-call
owning_repository: main-backend
consumer_repositories:
  - frontend
version: 1.0.0
compatibility_rule: additive-only
validation_method: contract-test
review_status: draft
```

## Authentication And Tenancy

- All endpoints require a Curriculum Admin, Subject Expert, or platform operator
  identity as defined by the Phase 0 identity and tenant baselines.
- Role enforcement is server-side: students and unrelated tenants MUST receive a
  denied-access response with an auditable event.

## Endpoint Groups

### Upload And Ingestion

| Method | Path | Who | Description |
|---|---|---|---|
| POST | `/admin/curriculum/upload` | Curriculum Admin | Upload a source file with curriculum type, grade, subject, academic year, and tutor language. Returns `job_id`. |
| GET | `/admin/curriculum/jobs/{job_id}` | Curriculum Admin | Ingestion job status. |
| GET | `/admin/curriculum/{source_id}/structure` | Curriculum Admin | Extracted chapter/topic/subtopic/lesson tree. |
| GET | `/admin/curriculum/{source_id}/structure/{lesson_id}` | Curriculum Admin | Extracted chunks for a specific lesson. |

### Asset Generation

| Method | Path | Who | Description |
|---|---|---|---|
| POST | `/admin/content/generate/batch` | Curriculum Admin | Trigger generation across a curriculum/subject/grade scope. |
| POST | `/admin/content/generate/{lesson_id}` | Curriculum Admin | Trigger generation for a single lesson. |
| GET | `/admin/content/jobs/{job_id}` | Curriculum Admin | Generation job status. |
| GET | `/admin/content/coverage` | Curriculum Admin / Operator | Coverage dashboard, filterable by curriculum type, grade, and subject. |

### Curriculum Admin Review (Tier 2)

| Method | Path | Who | Description |
|---|---|---|---|
| GET | `/admin/review/queue` | Curriculum Admin | Admin review queue with queue-age and filters. |
| GET | `/admin/review/{asset_id}` | Curriculum Admin | Full asset details, auto-validation results, source chunks. |
| POST | `/admin/review/{asset_id}/regenerate` | Curriculum Admin | Request regeneration scoped to a named pipeline stage. |
| PATCH | `/admin/review/{asset_id}/submit` | Curriculum Admin | Submit to expert review. |
| POST | `/admin/review/batch/submit` | Curriculum Admin | Submit multiple assets to expert review. |

### Subject Expert Assignment And Review (Tier 3)

| Method | Path | Who | Description |
|---|---|---|---|
| POST | `/admin/review/{asset_id}/assign` | Curriculum Admin | Assign to a subject expert. |
| POST | `/admin/review/batch/assign` | Curriculum Admin | Assign a batch of assets to a subject expert. |
| GET | `/admin/review/expert/{expert_id}/queue` | Curriculum Admin / Subject Expert | Expert's pending queue. |
| PATCH | `/admin/review/{asset_id}/approve` | Subject Expert | Approve and publish with a deterministic ID. |
| PATCH | `/admin/review/{asset_id}/reject` | Subject Expert | Reject with a required fix instruction. |
| PATCH | `/admin/review/{asset_id}/request-edit` | Subject Expert | Request an edit scoped to a named pipeline stage. |

### Update And Invalidation

| Method | Path | Who | Description |
|---|---|---|---|
| POST | `/admin/curriculum/{source_id}/update` | Curriculum Admin | Upload updated source; delta comparison re-processes only changed lessons. |
| PATCH | `/admin/content/{asset_id}/invalidate` | Curriculum Admin | Invalidate a live asset with reason; lesson falls back to safe mode. |

## Invariants

- Every state-changing endpoint records an audit event with actor, tenant scope,
  asset/lesson identifiers, outcome, timestamp, and correlation ID.
- Asset publication is only reachable through Subject Expert `approve`; no other
  endpoint may transition an asset to `active`.
- Concurrent approvals on the same asset are rejected with a conflict response.
- All responses carry the caller's correlation ID.
- Arabic and English responses are supported; direction-aware UI is handled client-
  side using the Phase 0 locale contract.

## Validation Method

- Contract test in `muallimi-main-backend/tests/contract` asserting schema, role
  enforcement, audit emission, and status transitions.
- Frontend end-to-end test in `Muaallimi-Platform/tests/e2e` walking upload → ingest →
  generate → admin review → expert approval → coverage dashboard.

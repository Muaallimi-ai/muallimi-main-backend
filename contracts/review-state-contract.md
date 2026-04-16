# Contract: Review State Transitions

## Purpose

Defines the allowed state machine for a `GeneratedAsset` through the Phase 1 review
pipeline. Used by the main-backend `Review` and `Publication` modules, consumed by the
frontend review screens and the document-ingestion regeneration path.

## Catalogue Record

```yaml
contract_id: curriculum.review.state
contract_type: service-call
owning_repository: main-backend
consumer_repositories:
  - frontend
  - document-ingestion
version: 1.0.0
compatibility_rule: breaking-change-review-required
validation_method: contract-test
review_status: draft
```

## States

- `queued`
- `producing`
- `auto_validating`
- `auto_failed`
- `pending_admin_review`
- `pending_expert_review`
- `approved`
- `rejected`
- `edit_requested`
- `invalidated`
- `superseded`

## Allowed Transitions

| From | To | Actor | Condition |
|---|---|---|---|
| `queued` | `producing` | system | generation worker picks up job |
| `producing` | `auto_validating` | system | generation stages complete |
| `auto_validating` | `auto_failed` | system | any blocking check fails |
| `auto_validating` | `pending_admin_review` | system | all blocking checks pass |
| `auto_failed` | `queued` | system | regeneration triggered |
| `pending_admin_review` | `pending_expert_review` | Curriculum Admin | Tier 2 approve |
| `pending_admin_review` | `queued` | Curriculum Admin | Tier 2 regeneration request |
| `pending_expert_review` | `approved` | Subject Expert | Tier 3 approve |
| `pending_expert_review` | `rejected` | Subject Expert | Tier 3 reject with fix instruction |
| `pending_expert_review` | `edit_requested` | Subject Expert | Tier 3 edit scoped to a stage |
| `edit_requested` | `producing` | system | targeted stage re-run |
| `rejected` | `queued` | system | full regeneration with fix instruction |
| `approved` | `invalidated` | system | lesson update or manual invalidation |
| `approved` | `superseded` | system | a newer version replaces it |

## Invariants

- `approved` is only reachable from `pending_expert_review`.
- No path from `auto_failed` leads directly to a review queue; regeneration is
  required first.
- Invalidation immediately removes the asset from runtime retrieval.
- A lesson cannot be marked student-ready if any required asset is not in `approved`.
- Every transition records an actor, timestamp, correlation ID, and (for `rejected`
  and `edit_requested`) a fix instruction.

## Validation Method

- Contract test in `muallimi-main-backend/tests/contract` covering every allowed and
  every forbidden transition.
- Observability assertion: no student-visible retrieval ever returns an asset whose
  current state is not `approved`.

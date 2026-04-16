# Contract: Content Event (`curriculum.lesson.indexed`)

## Purpose

Fired by the `muallimi-document-ingestion` worker when a new or changed lesson is
indexed. Triggers downstream production pipelines (audio, visual, quiz, Q&A cache).
Enforces the do-once principle: unchanged lessons produce no event.

## Catalogue Record (Phase 0 shape)

```yaml
contract_id: curriculum.lesson.indexed
contract_type: queue-message
owning_repository: document-ingestion
consumer_repositories:
  - main-backend
  - ai-service
version: 1.0.0
compatibility_rule: additive-only
validation_method: contract-test
review_status: draft
```

## Payload

```yaml
event_id: string                # uuid
lesson_id: string               # stable lesson identifier
curriculum_type: moe | language_school | international
grade: grade_7                  # MVP scope
subject: mathematics | science | arabic_language | english_language
tutor_language: ar | en         # additional approved languages per curriculum
content_hash: string            # hash of the extracted lesson content
change_kind: new_lesson | lesson_updated
occurred_at: iso-8601
correlation_id: string          # Phase 0 correlation-id shape
source_ref:
  source_id: string
  source_page_refs: [string]
```

## Routing And Delivery

- **Queue**: `curriculum.content-events` (logical name; Phase 0 queue contract).
- **Routing key**: `curriculum.lesson.indexed.{curriculum_type}.{subject}`.
- **At-least-once delivery** with idempotent consumer handling keyed on `event_id`.
- **Dead-letter queue**: `curriculum.content-events.dlq` after the Phase-0-defined
  retry policy is exhausted.

## Invariants

- `no_change` is never emitted.
- `content_hash` is stable for identical extracted content and MUST be used by
  consumers to short-circuit redundant work.
- `lesson_updated` events require that the previously published assets for the same
  `lesson_id` are invalidated by the main-backend publication module before new
  assets become retrievable.
- The event carries enough scope (`curriculum_type`, `subject`, `grade`,
  `tutor_language`) for consumers to enforce cross-curriculum isolation without
  additional lookups.

## Validation Method

- Contract test in `muallimi-document-ingestion/tests/contract` asserting schema,
  routing key, and idempotency on replay.
- Contract test in `muallimi-main-backend/tests/contract` asserting consumer reads
  the event, creates the generation job, and records the correlation ID.

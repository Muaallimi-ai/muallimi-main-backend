# Contract: Auto-Validation (Tier 1) Check Set

## Purpose

Defines the minimum blocking checks that every `GeneratedAsset` MUST pass before it can
enter Tier 2 (Curriculum Admin) review. Owned jointly by `muallimi-document-ingestion`
(which runs the checks as part of the generation pipeline) and `muallimi-ai-service`
(which provides the grounding, Arabic language quality, and cache-validation checks).

## Catalogue Record

```yaml
contract_id: curriculum.validation.tier1
contract_type: service-call
owning_repository: document-ingestion
consumer_repositories:
  - main-backend
  - ai-service
version: 1.0.0
compatibility_rule: additive-only
validation_method: contract-test
review_status: draft
```

## Check Set

| Check | Scope | Expected | On Failure |
|---|---|---|---|
| `grounding` | text, audio, visual, quiz, qa_cache | Narration and text claims supported by cited source chunks | Blocking — regenerate |
| `arabic_language_quality` | Arabic-language assets | MSA grammar, vocabulary, diacritic handling appropriate for the grade | Blocking — regenerate |
| `rendering_completeness` | visual | File duration within format bounds; HTML renders headless without errors; SVG valid schema | Blocking — regenerate |
| `narration_sync` | mp4_animation, whiteboard | Audio aligned to visual within 200ms | Blocking — regenerate |
| `curriculum_alignment` | all | Asset metadata (curriculum type, grade, subject, lesson) matches source chunk metadata | Blocking — regenerate |
| `file_integrity` | all media | MP4 not corrupted, HTML has no external dependencies, SVG well-formed | Blocking — regenerate |
| `accessibility` | audio, visual | Arabic transcript present; subtitle track present for MP4 and whiteboard | Blocking — regenerate |
| `semantic_similarity_cache` | qa_cache_entry | New entry does not duplicate an existing entry (similarity < 0.88 across unique questions) | Blocking — deduplicate |

## Inputs And Outputs

```yaml
request:
  asset_id: string
  asset_type: text_summary | audio | visual | quiz_item | qa_cache_entry
  scope: { curriculum_type, grade, subject, lesson_id, tutor_language }
  correlation_id: string

response:
  decision: passed | failed
  checks:
    - name: string
      status: passed | failed
      detail: string
  grounding_evidence:
    - source_chunk_id: string
      support_score: number
  validated_at: iso-8601
```

## Invariants

- Any `failed` blocking check sets the asset state to `auto_failed` and schedules
  regeneration; the asset MUST NOT enter a review queue in that run.
- The check set is additive-only: adding a check is allowed without a breaking
  version change, but removing a check requires a new major version and review.
- Auto-validation is idempotent: running it twice on the same asset must produce the
  same decision unless the underlying source chunks changed.

## Validation Method

- Contract test in `muallimi-document-ingestion/tests/contract` covering every check
  on a positive and negative fixture per asset type.
- Contract test in `muallimi-ai-service/tests/contract` covering grounding and
  Arabic language quality checks against curated Arabic test sets.

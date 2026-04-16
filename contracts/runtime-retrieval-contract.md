# Contract: Runtime Retrieval (Learning Engine Lookups Only)

## Purpose

Exposed by `muallimi-main-backend` to the Phase 2 learning engine. Provides approved
curriculum chunks, audio URLs, visual assets, quiz items, and Q&A cache hits scoped to
a curriculum type, grade, subject, and lesson. This contract is **lookup-only**: no
endpoint here triggers generation, and no endpoint returns unapproved or invalidated
assets.

## Catalogue Record

```yaml
contract_id: curriculum.runtime.retrieval
contract_type: service-call
owning_repository: main-backend
consumer_repositories:
  - ai-service      # learning engine consumes chunks, cache, visuals, audio
  - frontend        # indirect consumer through ai-service and main-backend
version: 1.0.0
compatibility_rule: backward-compatible
validation_method: contract-test
review_status: draft
```

## Endpoints

### POST `/internal/content/retrieve`

Returns top matching curriculum chunks with confidence scores, cache hit (if any),
approved visual assets, and audio URLs for the matched lesson.

**Request**

```yaml
query_text: string
scope:
  curriculum_type: moe | language_school | international
  grade: grade_7
  subject: mathematics | science | arabic_language | english_language
  tutor_language: ar | en
  active_lesson_id: string      # optional, used to bias scoping
max_chunks: integer             # default 5
correlation_id: string
```

**Response**

```yaml
chunks:
  - chunk_id: string
    text: string
    metadata: { topic, subtopic, lesson_id, source_refs, academic_year }
    confidence: number          # cosine similarity 0.0–1.0
qa_cache_hit:
  entry_id: string | null
  question_text: string
  answer_text: string
  similarity: number
audio:
  - chunk_id: string
    url: string                 # logical CDN URL
visuals:
  - lesson_id: string
    asset_id: string
    format: mp4_animation | interactive_html | whiteboard | diagram
    url: string
    transcript_url: string
```

### GET `/internal/content/visual/{lesson_id}`

Returns an array of approved visual assets for a lesson (possibly empty).

### GET `/internal/content/audio/{chunk_id}`

Returns the approved audio URL for a chunk, or null if none exists.

## Invariants

- Only `active` published assets are returned.
- Cross-scope leakage (another curriculum type, grade, or subject) is a contract
  failure.
- No endpoint in this contract may trigger generation, publication, invalidation, or
  any write to curriculum state.
- Every response carries the request's `correlation_id` back in the response header.

## Validation Method

- Contract test in `muallimi-main-backend/tests/contract` asserting scope filtering,
  lookup-only behavior, and correlation ID propagation.
- Observability assertion: zero outbound generation calls during retrieval test runs.

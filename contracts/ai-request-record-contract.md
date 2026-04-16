# AI Request Record Contract (Phase 2)

> **Canonical source**: [specs/004-ai-tutor-rag/contracts/ai-request-record-contract.md](../../Muaallimi-Platform-Planning-Docs-main/specs/004-ai-tutor-rag/contracts/ai-request-record-contract.md)

**Owner**: `muallimi-ai-service` (producer)
**Consumer**: `muallimi-main-backend` (persistence + AI operations query surface)

## Event
`ai.tutor.request.recorded` (queue topic)

## Payload
- `record_id`, `correlation_id`, `session_id`, `tenant_id`
- `curriculum_scope { type, grade, subject, tutor_language, session_mode }`
- `stages[]` — ordered `GuardrailOutcome` per stage with `{prompt_id, version_id}` captured
- `routing_decision` — `{ chosen_source, cache_match_score, confidence_signal, model_tier, provider_identifier }`
- `input_token_count`, `output_token_count`, `latency_ms`, `cache_match_score`
- `final_outcome` — `answered | refused | fallback_redirect`
- `question_text_preview` (redacted outside `incident_investigation` role)
- `prompt_versions_used[]` — `[{stage, prompt_id, version_id}, ...]`

Idempotent on replay (`record_id` unique).

# Tutor Runtime Contract (Phase 2)

> **Canonical source**: [specs/004-ai-tutor-rag/contracts/tutor-runtime-contract.md](../../Muaallimi-Platform-Planning-Docs-main/specs/004-ai-tutor-rag/contracts/tutor-runtime-contract.md)
>
> This file is a pinned consumer reference used by `muallimi-ai-service`. Do not diverge from the canonical spec. When the canonical changes, update this reference and re-run contract tests under `tests/contract/`.

**Owner**: `muallimi-ai-service` (runtime)
**Consumers**: `muallimi-main-backend` (tutor exposure facade), `Muaallimi-Platform` (student chat UI — Phase 3)

## Request Envelope
- `session_scope` — `{tenant_id, session_id, curriculum_type, grade, subject, tutor_language, session_mode}`
- `question_text` (UTF-8), optional `question_audio_ref`
- `correlation_id` (Phase 0 baseline)

## Response Envelope
- `answer` | `refusal` | `fallback_redirect`
- `routing_metadata.record_id` — resolves to `AiRequestRecord`
- `evidence_refs[]` — approved chunk IDs (non-empty on answer)
- `confidence_signal` — `cache_hit | high | borderline | low`
- `model_tier` — `cache | lightweight | stronger | refused`

See canonical for full field tables, refusal envelope shape, and error handling.

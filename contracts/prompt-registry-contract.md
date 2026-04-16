# Prompt Registry Contract (Phase 2)

> **Canonical source**: [specs/004-ai-tutor-rag/contracts/prompt-registry-contract.md](../../Muaallimi-Platform-Planning-Docs-main/specs/004-ai-tutor-rag/contracts/prompt-registry-contract.md)

**Owner**: `muallimi-ai-service` (read path), `muallimi-main-backend` (write path + audit persistence)

## Entities
- `Prompt { prompt_id, name, purpose, scope, active_version_id }`
- `PromptVersion { version_id, prompt_id, version_number (monotonic), body (immutable), declared_variables[], created_by, created_at }`
- `PromptAuditEntry { entry_id, prompt_id, version_id, action: created|promoted|archived|rolledback, actor, correlation_id, at }`

## Read Path (ai-service)
Fetch active version by `(prompt_id, scope)` with Redis cache. Invalidate on `prompt.promoted | prompt.archived | prompt.rolledback` events. Loud failure if a declared variable is missing at render time (FR-013).

## Write Path (main-backend)
- `POST /internal/prompts` — create
- `POST /internal/prompts/{id}/versions` — new version (immutable body)
- `POST /internal/prompts/{id}/promote` — promote version (rejected if `promotion_block_flag=true`)
- `POST /internal/prompts/{id}/archive` — archive
- `POST /internal/prompts/{id}/rollback` — rollback to prior version

Every endpoint emits a `PromptAuditEntry`.

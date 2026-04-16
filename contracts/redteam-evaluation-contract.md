# Red-Team Evaluation Contract (Phase 2)

> **Canonical source**: [specs/004-ai-tutor-rag/contracts/redteam-evaluation-contract.md](../../Muaallimi-Platform-Planning-Docs-main/specs/004-ai-tutor-rag/contracts/redteam-evaluation-contract.md)

**Owner**: `muallimi-ai-service` (runner), `muallimi-main-backend` (result persistence, promotion block enforcement)

## Entities
- `RedTeamScenarioSet { set_id, version, scenarios[] }` — versioned in local blob storage.
- `RedTeamEvaluationResult { result_id, set_id, set_version, run_at, pass_count, fail_count, regressions[], promotion_block_flag }`

## Attack Categories (readiness gate)
prompt_injection, system_prompt_leakage, role_inversion, encoded_payloads, multilingual_injection, instruction_override, homework_solving_coercion, tool_misuse, cross_curriculum_exfiltration

## Endpoint
`POST /internal/redteam/runs` — triggers run against active configuration. 100% pass required at readiness gate. Regressions set `promotion_block_flag=true` on affected `Prompt` and `ProviderAdapterBinding` records.

Arabic, English, and bilingual scenarios MUST have equal pass rates (SC-012).

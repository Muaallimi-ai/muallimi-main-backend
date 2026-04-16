# Guardrail Chain Contract (Phase 2)

> **Canonical source**: [specs/004-ai-tutor-rag/contracts/guardrail-chain-contract.md](../../Muaallimi-Platform-Planning-Docs-main/specs/004-ai-tutor-rag/contracts/guardrail-chain-contract.md)

**Owner**: `muallimi-ai-service`

## Fixed Stage Order
1. `input_language`
2. `scope`
3. `safety_pii`
4. `retrieval`
5. `grounding`
6. `routing`
7. `generation`
8. `output_safety`

Each stage emits a `GuardrailOutcome { stage, decision: pass|refuse|block|rewrite, reason_code, latency_ms }`. A refuse/block terminates the chain; `output_safety` cannot run if `generation` did not run. Zero live generation tokens for pre-`generation` refusals.

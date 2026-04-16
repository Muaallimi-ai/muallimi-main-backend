# Provider Adapter Contract (Phase 2)

> **Canonical source**: [specs/004-ai-tutor-rag/contracts/provider-adapter-contract.md](../../Muaallimi-Platform-Planning-Docs-main/specs/004-ai-tutor-rag/contracts/provider-adapter-contract.md)

**Owner**: `muallimi-ai-service`

## Capabilities
`llm_lightweight`, `llm_stronger`, `embedding`, `stt`, `tts`, `voice_profile`

## Contract
- Each capability has a swappable adapter behind a stable interface (`ILlmAdapter`, `IEmbeddingAdapter`, `ISttAdapter`, `ITtsAdapter`, `IVoiceProfileAdapter`).
- `ProviderAdapterBinding { binding_id, capability, environment, curriculum_scope, provider_identifier, fallback_chain[], active }` — unique active per `(capability, environment, curriculum_scope)`.
- Fallback chain invoked on timeout/quota/provider errors; failure + fallback logged into `AiRequestRecord`.
- Local mode operates without managed cloud credentials.
- AI tutor `voice_profile.profile_identifier` MUST differ from every Phase 1 teacher voice profile identifier (FR-019).

# Phase 2 Migration

`Phase2_AiTutor.cs` is the canonical Phase 2 schema migration referenced by
`specs/004-ai-tutor-rag/tasks.md` (T007). To apply it inside the EF toolchain:

1. Copy `Phase2_AiTutor.cs` into `src/Muallimi.Infrastructure/Migrations/` with a
   timestamp prefix (e.g. `20260501000000_Phase2_AiTutor.cs`), or configure
   `MigrationsAssembly` to include this folder.
2. Run `dotnet ef migrations script 20260416155532_InitialCreate 20260501000000_Phase2_AiTutor`
   (adjust timestamp) from the repo root to validate the generated SQL.
3. Apply locally via `dotnet ef database update --project src/Muallimi.Infrastructure`.

Tables introduced: `prompts`, `prompt_versions`, `prompt_audit_entries`,
`provider_adapter_bindings`, `ai_request_records`, `refusal_events`,
`ai_operations_metrics`, `red_team_scenario_sets`, `red_team_evaluation_results`.

Indexes introduced:
- `ai_request_records(correlation_id, session_id, curriculum_type, final_outcome)`
- `prompt_versions(prompt_id, version_number)` UNIQUE
- `provider_adapter_bindings(capability, environment, curriculum_scope, active)`
- `prompt_audit_entries(prompt_id)`
- `refusal_events(record_id)`
- `prompts(name)` UNIQUE

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muallimi.Infrastructure.Migrations;

/// <summary>
/// Phase 2 - AI Tutor, RAG, Prompts, Guardrails.
/// Adds prompt registry, provider adapter bindings, AI request records,
/// refusal events, AI operations metrics, and red-team evaluation tables.
/// Indexes:
///   - ai_request_records(correlation_id, session_id, curriculum_type, final_outcome)
///   - prompt_versions(prompt_id, version_number) UNIQUE
///   - provider_adapter_bindings(capability, environment, curriculum_scope, active)
///
/// To wire into EF:
///   1. Move this file to src/Muallimi.Infrastructure/Migrations/ (or configure
///      MigrationsAssembly to include db/Migrations/).
///   2. Run `dotnet ef migrations script` to verify SQL output.
/// </summary>
public partial class Phase2_AiTutor : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // ── prompts ──
        migrationBuilder.CreateTable(
            name: "prompts",
            columns: table => new
            {
                prompt_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "text", nullable: false),
                purpose = table.Column<string>(type: "text", nullable: false),
                scope = table.Column<string>(type: "text", nullable: false),
                active_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                promotion_block_flag = table.Column<bool>(type: "boolean", nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_prompts", x => x.prompt_id));
        migrationBuilder.CreateIndex("IX_prompts_name", "prompts", "name", unique: true);

        // ── prompt_versions ──
        migrationBuilder.CreateTable(
            name: "prompt_versions",
            columns: table => new
            {
                version_id = table.Column<Guid>(type: "uuid", nullable: false),
                prompt_id = table.Column<Guid>(type: "uuid", nullable: false),
                version_number = table.Column<int>(type: "integer", nullable: false),
                body = table.Column<string>(type: "text", nullable: false),
                declared_variables = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                created_by = table.Column<string>(type: "text", nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                status = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_prompt_versions", x => x.version_id));
        migrationBuilder.CreateIndex("IX_prompt_versions_prompt_id_version_number", "prompt_versions",
            new[] { "prompt_id", "version_number" }, unique: true);

        // ── prompt_audit_entries ──
        migrationBuilder.CreateTable(
            name: "prompt_audit_entries",
            columns: table => new
            {
                entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                prompt_id = table.Column<Guid>(type: "uuid", nullable: false),
                version_id = table.Column<Guid>(type: "uuid", nullable: true),
                action = table.Column<string>(type: "text", nullable: false),
                actor = table.Column<string>(type: "text", nullable: false),
                correlation_id = table.Column<string>(type: "text", nullable: true),
                occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                diff = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_prompt_audit_entries", x => x.entry_id));
        migrationBuilder.CreateIndex("IX_prompt_audit_entries_prompt_id", "prompt_audit_entries", "prompt_id");

        // ── provider_adapter_bindings ──
        migrationBuilder.CreateTable(
            name: "provider_adapter_bindings",
            columns: table => new
            {
                binding_id = table.Column<Guid>(type: "uuid", nullable: false),
                capability = table.Column<string>(type: "text", nullable: false),
                environment = table.Column<string>(type: "text", nullable: false),
                curriculum_scope = table.Column<string>(type: "text", nullable: true),
                provider_identifier = table.Column<string>(type: "text", nullable: false),
                fallback_chain = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                active = table.Column<bool>(type: "boolean", nullable: false),
                promotion_block_flag = table.Column<bool>(type: "boolean", nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_provider_adapter_bindings", x => x.binding_id));
        migrationBuilder.CreateIndex("IX_provider_adapter_bindings_cap_env_scope_active",
            "provider_adapter_bindings",
            new[] { "capability", "environment", "curriculum_scope", "active" });

        // ── ai_request_records ──
        migrationBuilder.CreateTable(
            name: "ai_request_records",
            columns: table => new
            {
                record_id = table.Column<Guid>(type: "uuid", nullable: false),
                correlation_id = table.Column<string>(type: "text", nullable: false),
                session_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                curriculum_type = table.Column<string>(type: "text", nullable: false),
                grade = table.Column<string>(type: "text", nullable: false),
                subject = table.Column<string>(type: "text", nullable: false),
                tutor_language = table.Column<string>(type: "text", nullable: false),
                session_mode = table.Column<string>(type: "text", nullable: false),
                stages = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                routing_decision = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                input_token_count = table.Column<int>(type: "integer", nullable: false),
                output_token_count = table.Column<int>(type: "integer", nullable: false),
                latency_ms = table.Column<int>(type: "integer", nullable: false),
                cache_match_score = table.Column<double>(type: "double precision", nullable: true),
                final_outcome = table.Column<string>(type: "text", nullable: false),
                question_text_preview = table.Column<string>(type: "text", nullable: true),
                prompt_versions_used = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ai_request_records", x => x.record_id));
        migrationBuilder.CreateIndex("IX_ai_request_records_correlation_session_scope_outcome",
            "ai_request_records",
            new[] { "correlation_id", "session_id", "curriculum_type", "final_outcome" });

        // ── refusal_events ──
        migrationBuilder.CreateTable(
            name: "refusal_events",
            columns: table => new
            {
                event_id = table.Column<Guid>(type: "uuid", nullable: false),
                record_id = table.Column<Guid>(type: "uuid", nullable: false),
                stage = table.Column<string>(type: "text", nullable: false),
                reason_code = table.Column<string>(type: "text", nullable: false),
                localised_reason = table.Column<string>(type: "text", nullable: false),
                tutor_language = table.Column<string>(type: "text", nullable: false),
                occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_refusal_events", x => x.event_id));
        migrationBuilder.CreateIndex("IX_refusal_events_record_id", "refusal_events", "record_id");

        // ── ai_operations_metrics ──
        migrationBuilder.CreateTable(
            name: "ai_operations_metrics",
            columns: table => new
            {
                metric_id = table.Column<Guid>(type: "uuid", nullable: false),
                window_start = table.Column<string>(type: "text", nullable: false),
                window_end = table.Column<string>(type: "text", nullable: false),
                curriculum_type = table.Column<string>(type: "text", nullable: false),
                grade = table.Column<string>(type: "text", nullable: false),
                subject = table.Column<string>(type: "text", nullable: false),
                tutor_language = table.Column<string>(type: "text", nullable: false),
                session_mode = table.Column<string>(type: "text", nullable: false),
                volume = table.Column<long>(type: "bigint", nullable: false),
                refusal_rate = table.Column<double>(type: "double precision", nullable: false),
                cache_hit_rate = table.Column<double>(type: "double precision", nullable: false),
                grounded_answer_rate = table.Column<double>(type: "double precision", nullable: false),
                per_branch = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                prompt_version_distribution = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                computed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ai_operations_metrics", x => x.metric_id));

        // ── red_team_scenario_sets ──
        migrationBuilder.CreateTable(
            name: "red_team_scenario_sets",
            columns: table => new
            {
                set_id = table.Column<Guid>(type: "uuid", nullable: false),
                version = table.Column<string>(type: "text", nullable: false),
                storage_key = table.Column<string>(type: "text", nullable: false),
                scenario_count = table.Column<int>(type: "integer", nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_red_team_scenario_sets", x => x.set_id));

        // ── red_team_evaluation_results ──
        migrationBuilder.CreateTable(
            name: "red_team_evaluation_results",
            columns: table => new
            {
                result_id = table.Column<Guid>(type: "uuid", nullable: false),
                set_id = table.Column<Guid>(type: "uuid", nullable: false),
                set_version = table.Column<string>(type: "text", nullable: false),
                run_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                pass_count = table.Column<int>(type: "integer", nullable: false),
                fail_count = table.Column<int>(type: "integer", nullable: false),
                regressions = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                promotion_block_flag = table.Column<bool>(type: "boolean", nullable: false),
                correlation_id = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_red_team_evaluation_results", x => x.result_id));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("red_team_evaluation_results");
        migrationBuilder.DropTable("red_team_scenario_sets");
        migrationBuilder.DropTable("ai_operations_metrics");
        migrationBuilder.DropTable("refusal_events");
        migrationBuilder.DropTable("ai_request_records");
        migrationBuilder.DropTable("provider_adapter_bindings");
        migrationBuilder.DropTable("prompt_audit_entries");
        migrationBuilder.DropTable("prompt_versions");
        migrationBuilder.DropTable("prompts");
    }
}

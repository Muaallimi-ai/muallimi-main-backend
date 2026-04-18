using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muallimi.Infrastructure.Migrations;

/// <summary>
/// Phase 4 — Engagement, Progress, and Parent Support.
///
/// Adds tables for the Phase 3 session-event-to-progress pipeline, mastery,
/// streak, badge, focus-area, weekly report, parent dashboard/notification,
/// operator impersonation audit, at-risk detection, intervention prompt, the
/// guardrail decision trail store, and the Phase 5-facing downstream outbox:
///   - progress_records
///   - mastery_states
///   - streak_states
///   - badge_criteria
///   - badge_awards
///   - focus_areas
///   - weekly_reports
///   - guardrail_decision_trails
///   - parent_profiles
///   - child_links
///   - parent_notifications
///   - operator_impersonation_audits
///   - at_risk_flags
///   - intervention_prompts
///   - phase4_downstream_events
///
/// Indexes (see data-model.md):
///   - progress_records UNIQUE(tenant_id, source_event_id)         idempotency
///   - mastery_states   UNIQUE(tenant_id, student_id, subject_id, topic_id,
///                             calculation_version)
///   - streak_states    UNIQUE(tenant_id, student_id)
///   - badge_criteria   UNIQUE(badge_key, version)
///   - badge_awards     UNIQUE(tenant_id, student_id, badge_criterion_id,
///                             badge_criterion_version)
///   - parent_profiles  UNIQUE(tenant_id, identity_id)
///   - child_links      UNIQUE(tenant_id, parent_profile_id, student_id)
///   - weekly_reports   UNIQUE(tenant_id, student_id, window_start, window_end)
///   - phase4_downstream_events INDEX(delivery_state, occurred_at)
///   - phase4_downstream_events INDEX(correlation_id)
///
/// To wire into EF:
///   1. Move this file to src/Muallimi.Infrastructure/Migrations/.
///   2. Run `dotnet ef migrations script` to verify SQL output.
/// </summary>
public partial class Phase4_Engagement : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "progress_records",
            columns: table => new
            {
                progress_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                student_id = table.Column<Guid>(type: "uuid", nullable: false),
                source_event_id = table.Column<string>(type: "text", nullable: false),
                event_kind = table.Column<string>(type: "text", nullable: false),
                curriculum_scope = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                payload = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                correlation_id = table.Column<string>(type: "text", nullable: false),
                occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ingested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_progress_records", x => x.progress_record_id));
        migrationBuilder.CreateIndex(
            name: "UX_progress_records_tenant_source_event",
            table: "progress_records",
            columns: new[] { "tenant_id", "source_event_id" },
            unique: true);
        migrationBuilder.CreateIndex("IX_progress_records_student", "progress_records", new[] { "tenant_id", "student_id" });
        migrationBuilder.CreateIndex("IX_progress_records_correlation_id", "progress_records", "correlation_id");

        migrationBuilder.CreateTable(
            name: "mastery_states",
            columns: table => new
            {
                mastery_state_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                student_id = table.Column<Guid>(type: "uuid", nullable: false),
                curriculum_type = table.Column<string>(type: "text", nullable: false),
                subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                topic_id = table.Column<Guid>(type: "uuid", nullable: true),
                mastery_score = table.Column<decimal>(type: "numeric(6,4)", nullable: false),
                mastery_band = table.Column<string>(type: "text", nullable: false),
                calculation_version = table.Column<string>(type: "text", nullable: false),
                sample_window_start = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                sample_window_end = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                contributing_record_count = table.Column<int>(type: "integer", nullable: false),
                last_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                last_correlation_id = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_mastery_states", x => x.mastery_state_id));
        migrationBuilder.CreateIndex(
            name: "UX_mastery_states_student_scope_version",
            table: "mastery_states",
            columns: new[] { "tenant_id", "student_id", "subject_id", "topic_id", "calculation_version" },
            unique: true);

        migrationBuilder.CreateTable(
            name: "streak_states",
            columns: table => new
            {
                streak_state_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                student_id = table.Column<Guid>(type: "uuid", nullable: false),
                current_length = table.Column<int>(type: "integer", nullable: false),
                longest_length = table.Column<int>(type: "integer", nullable: false),
                last_qualifying_day = table.Column<DateTime>(type: "date", nullable: false),
                family_timezone = table.Column<string>(type: "text", nullable: false),
                reset_history = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                last_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_streak_states", x => x.streak_state_id));
        migrationBuilder.CreateIndex(
            name: "UX_streak_states_tenant_student",
            table: "streak_states",
            columns: new[] { "tenant_id", "student_id" },
            unique: true);

        migrationBuilder.CreateTable(
            name: "badge_criteria",
            columns: table => new
            {
                badge_criterion_id = table.Column<Guid>(type: "uuid", nullable: false),
                badge_key = table.Column<string>(type: "text", nullable: false),
                version = table.Column<string>(type: "text", nullable: false),
                category = table.Column<string>(type: "text", nullable: false),
                display_name_ar = table.Column<string>(type: "text", nullable: false),
                display_name_en = table.Column<string>(type: "text", nullable: false),
                description_ar = table.Column<string>(type: "text", nullable: false),
                description_en = table.Column<string>(type: "text", nullable: false),
                threshold = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                retired_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_badge_criteria", x => x.badge_criterion_id));
        migrationBuilder.CreateIndex(
            name: "UX_badge_criteria_key_version",
            table: "badge_criteria",
            columns: new[] { "badge_key", "version" },
            unique: true);

        migrationBuilder.CreateTable(
            name: "badge_awards",
            columns: table => new
            {
                badge_award_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                student_id = table.Column<Guid>(type: "uuid", nullable: false),
                badge_criterion_id = table.Column<Guid>(type: "uuid", nullable: false),
                badge_criterion_version = table.Column<string>(type: "text", nullable: false),
                awarded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                originating_progress_record_ids = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                celebration_shown = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                correlation_id = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_badge_awards", x => x.badge_award_id));
        migrationBuilder.CreateIndex(
            name: "UX_badge_awards_unique_per_criterion_version",
            table: "badge_awards",
            columns: new[] { "tenant_id", "student_id", "badge_criterion_id", "badge_criterion_version" },
            unique: true);

        migrationBuilder.CreateTable(
            name: "focus_areas",
            columns: table => new
            {
                focus_area_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                student_id = table.Column<Guid>(type: "uuid", nullable: false),
                curriculum_type = table.Column<string>(type: "text", nullable: false),
                subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                chapter_id = table.Column<Guid>(type: "uuid", nullable: false),
                topic_id = table.Column<Guid>(type: "uuid", nullable: false),
                signal_summary = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                rationale_ar = table.Column<string>(type: "text", nullable: false),
                rationale_en = table.Column<string>(type: "text", nullable: false),
                suggested_next_step = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                guardrail_decision_trail_id = table.Column<Guid>(type: "uuid", nullable: false),
                computed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                valid_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                correlation_id = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_focus_areas", x => x.focus_area_id));
        migrationBuilder.CreateIndex("IX_focus_areas_student", "focus_areas", new[] { "tenant_id", "student_id" });

        migrationBuilder.CreateTable(
            name: "guardrail_decision_trails",
            columns: table => new
            {
                guardrail_decision_trail_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                artefact_kind = table.Column<string>(type: "text", nullable: false),
                artefact_id = table.Column<Guid>(type: "uuid", nullable: false),
                prompt_key = table.Column<string>(type: "text", nullable: false),
                chain_output = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                final_stage = table.Column<string>(type: "text", nullable: false),
                language = table.Column<string>(type: "text", nullable: false),
                correlation_id = table.Column<string>(type: "text", nullable: false),
                captured_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_guardrail_decision_trails", x => x.guardrail_decision_trail_id));
        migrationBuilder.CreateIndex("IX_guardrail_decision_trails_artefact", "guardrail_decision_trails", new[] { "artefact_kind", "artefact_id" });

        migrationBuilder.CreateTable(
            name: "weekly_reports",
            columns: table => new
            {
                weekly_report_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                student_id = table.Column<Guid>(type: "uuid", nullable: false),
                window_start = table.Column<DateTime>(type: "date", nullable: false),
                window_end = table.Column<DateTime>(type: "date", nullable: false),
                generated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                run_id = table.Column<Guid>(type: "uuid", nullable: false),
                mastery_deltas = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                top_focus_areas = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                awarded_badges = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                summary_ar = table.Column<string>(type: "text", nullable: false),
                summary_en = table.Column<string>(type: "text", nullable: false),
                guardrail_decision_trail_id = table.Column<Guid>(type: "uuid", nullable: false),
                evidence_refs = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                share_token_hash = table.Column<string>(type: "text", nullable: true),
                correlation_id = table.Column<string>(type: "text", nullable: false),
                status = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_weekly_reports", x => x.weekly_report_id));
        migrationBuilder.CreateIndex(
            name: "UX_weekly_reports_child_window",
            table: "weekly_reports",
            columns: new[] { "tenant_id", "student_id", "window_start", "window_end" },
            unique: true);

        migrationBuilder.CreateTable(
            name: "parent_profiles",
            columns: table => new
            {
                parent_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                preferred_language = table.Column<string>(type: "text", nullable: false),
                locale = table.Column<string>(type: "text", nullable: false),
                timezone = table.Column<string>(type: "text", nullable: false),
                notification_channels = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                quiet_hours = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                per_child_overrides = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                consent_state = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_parent_profiles", x => x.parent_profile_id));
        migrationBuilder.CreateIndex(
            name: "UX_parent_profiles_tenant_identity",
            table: "parent_profiles",
            columns: new[] { "tenant_id", "identity_id" },
            unique: true);

        migrationBuilder.CreateTable(
            name: "child_links",
            columns: table => new
            {
                child_link_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                parent_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                student_id = table.Column<Guid>(type: "uuid", nullable: false),
                role = table.Column<string>(type: "text", nullable: false),
                effective_start = table.Column<DateTime>(type: "date", nullable: false),
                effective_end = table.Column<DateTime>(type: "date", nullable: true),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_child_links", x => x.child_link_id));
        migrationBuilder.CreateIndex(
            name: "UX_child_links_parent_child",
            table: "child_links",
            columns: new[] { "tenant_id", "parent_profile_id", "student_id" },
            unique: true);

        migrationBuilder.CreateTable(
            name: "parent_notifications",
            columns: table => new
            {
                parent_notification_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                parent_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                child_id = table.Column<Guid>(type: "uuid", nullable: false),
                notification_kind = table.Column<string>(type: "text", nullable: false),
                channel = table.Column<string>(type: "text", nullable: false),
                language = table.Column<string>(type: "text", nullable: false),
                body_ar = table.Column<string>(type: "text", nullable: true),
                body_en = table.Column<string>(type: "text", nullable: true),
                quiet_hours_deferred_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                dispatched_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                delivery_state = table.Column<string>(type: "text", nullable: false),
                correlation_id = table.Column<string>(type: "text", nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_parent_notifications", x => x.parent_notification_id));
        migrationBuilder.CreateIndex("IX_parent_notifications_queue", "parent_notifications", new[] { "delivery_state", "created_at" });

        migrationBuilder.CreateTable(
            name: "operator_impersonation_audits",
            columns: table => new
            {
                operator_impersonation_audit_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                operator_actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                target_parent_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                target_child_id = table.Column<Guid>(type: "uuid", nullable: true),
                surface = table.Column<string>(type: "text", nullable: false),
                reason = table.Column<string>(type: "text", nullable: false),
                correlation_id = table.Column<string>(type: "text", nullable: false),
                viewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_operator_impersonation_audits", x => x.operator_impersonation_audit_id));
        migrationBuilder.CreateIndex("IX_operator_impersonation_audits_target", "operator_impersonation_audits", new[] { "tenant_id", "target_parent_profile_id", "viewed_at" });

        migrationBuilder.CreateTable(
            name: "at_risk_flags",
            columns: table => new
            {
                at_risk_flag_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                student_id = table.Column<Guid>(type: "uuid", nullable: false),
                threshold_version = table.Column<string>(type: "text", nullable: false),
                triggering_evidence = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                raised_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                cleared_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                linked_intervention_prompt_id = table.Column<Guid>(type: "uuid", nullable: true),
                correlation_id = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_at_risk_flags", x => x.at_risk_flag_id));
        migrationBuilder.CreateIndex("IX_at_risk_flags_active", "at_risk_flags", new[] { "tenant_id", "student_id", "cleared_at" });

        migrationBuilder.CreateTable(
            name: "intervention_prompts",
            columns: table => new
            {
                intervention_prompt_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                student_id = table.Column<Guid>(type: "uuid", nullable: false),
                originating_flag_id = table.Column<Guid>(type: "uuid", nullable: true),
                originating_focus_area_id = table.Column<Guid>(type: "uuid", nullable: true),
                body_ar = table.Column<string>(type: "text", nullable: false),
                body_en = table.Column<string>(type: "text", nullable: false),
                next_step = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                guardrail_decision_trail_id = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                correlation_id = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_intervention_prompts", x => x.intervention_prompt_id));
        migrationBuilder.CreateIndex("IX_intervention_prompts_student", "intervention_prompts", new[] { "tenant_id", "student_id", "created_at" });

        migrationBuilder.CreateTable(
            name: "phase4_downstream_events",
            columns: table => new
            {
                phase4_downstream_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                event_kind = table.Column<string>(type: "text", nullable: false),
                student_id = table.Column<Guid>(type: "uuid", nullable: false),
                scope = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                payload = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                correlation_id = table.Column<string>(type: "text", nullable: false),
                occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                dispatched_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                delivery_state = table.Column<string>(type: "text", nullable: false),
                dispatch_attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
            },
            constraints: table => table.PrimaryKey("PK_phase4_downstream_events", x => x.phase4_downstream_event_id));
        migrationBuilder.CreateIndex("IX_phase4_downstream_events_queue", "phase4_downstream_events", new[] { "delivery_state", "occurred_at" });
        migrationBuilder.CreateIndex("IX_phase4_downstream_events_correlation", "phase4_downstream_events", "correlation_id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("phase4_downstream_events");
        migrationBuilder.DropTable("intervention_prompts");
        migrationBuilder.DropTable("at_risk_flags");
        migrationBuilder.DropTable("operator_impersonation_audits");
        migrationBuilder.DropTable("parent_notifications");
        migrationBuilder.DropTable("child_links");
        migrationBuilder.DropTable("parent_profiles");
        migrationBuilder.DropTable("weekly_reports");
        migrationBuilder.DropTable("guardrail_decision_trails");
        migrationBuilder.DropTable("focus_areas");
        migrationBuilder.DropTable("badge_awards");
        migrationBuilder.DropTable("badge_criteria");
        migrationBuilder.DropTable("streak_states");
        migrationBuilder.DropTable("mastery_states");
        migrationBuilder.DropTable("progress_records");
    }
}

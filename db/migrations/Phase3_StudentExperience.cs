using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muallimi.Infrastructure.Migrations;

/// <summary>
/// Phase 3 - Student Learning Experience.
///
/// Adds tables for student profile, session scope, mode-specific state,
/// plan gating, and the Phase 4-facing session event outbox:
///   - student_profiles
///   - student_sessions
///   - lesson_viewer_states
///   - tutor_chat_messages
///   - voice_captures
///   - quiz_sessions
///   - mock_test_sessions
///   - homework_help_submissions
///   - whiteboard_sessions
///   - plan_gate_policies
///   - session_events
///
/// Indexes (see data-model.md):
///   - student_sessions(tenant_id, student_profile_id)
///   - student_sessions(correlation_id)
///   - tutor_chat_messages(student_session_id, turn_number)
///   - session_events(dispatch_state, created_at)
///   - session_events(correlation_id)
///   - plan_gate_policies(mode, tenant_id, enabled_at)
///
/// To wire into EF:
///   1. Move this file to src/Muallimi.Infrastructure/Migrations/.
///   2. Run `dotnet ef migrations script` to verify SQL output.
/// </summary>
public partial class Phase3_StudentExperience : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // ── student_profiles ──
        migrationBuilder.CreateTable(
            name: "student_profiles",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                display_name = table.Column<string>(type: "text", nullable: false),
                avatar_reference = table.Column<string>(type: "text", nullable: true),
                curriculum_type = table.Column<string>(type: "text", nullable: false),
                grade = table.Column<string>(type: "text", nullable: false),
                preferred_language = table.Column<string>(type: "text", nullable: false),
                rtl_override = table.Column<bool>(type: "boolean", nullable: true),
                plan_tier = table.Column<string>(type: "text", nullable: false),
                subjects_enrolled = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                consent_state = table.Column<string>(type: "text", nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_student_profiles", x => x.id));
        migrationBuilder.CreateIndex("IX_student_profiles_tenant_id", "student_profiles", "tenant_id");

        // ── student_sessions ──
        migrationBuilder.CreateTable(
            name: "student_sessions",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                student_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                active_curriculum_type = table.Column<string>(type: "text", nullable: true),
                active_grade = table.Column<string>(type: "text", nullable: true),
                active_subject_id = table.Column<Guid>(type: "uuid", nullable: true),
                active_chapter_id = table.Column<Guid>(type: "uuid", nullable: true),
                active_topic_id = table.Column<Guid>(type: "uuid", nullable: true),
                active_lesson_id = table.Column<Guid>(type: "uuid", nullable: true),
                active_mode = table.Column<string>(type: "text", nullable: false),
                tutor_language = table.Column<string>(type: "text", nullable: false),
                device_class = table.Column<string>(type: "text", nullable: false),
                plan_tier_snapshot = table.Column<string>(type: "text", nullable: false),
                session_started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                session_last_activity_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                session_ended_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                end_reason = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_student_sessions", x => x.id));
        migrationBuilder.CreateIndex("IX_student_sessions_tenant_id_student_profile_id", "student_sessions",
            new[] { "tenant_id", "student_profile_id" });
        migrationBuilder.CreateIndex("IX_student_sessions_correlation_id", "student_sessions", "correlation_id");

        // ── lesson_viewer_states ──
        migrationBuilder.CreateTable(
            name: "lesson_viewer_states",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                student_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                lesson_id = table.Column<Guid>(type: "uuid", nullable: false),
                viewer_position = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                playback_state = table.Column<string>(type: "text", nullable: false),
                teacher_voice_profile_id = table.Column<string>(type: "text", nullable: true),
                captions_enabled = table.Column<bool>(type: "boolean", nullable: false),
                rate = table.Column<double>(type: "double precision", nullable: false),
                last_interaction_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_lesson_viewer_states", x => x.id));
        migrationBuilder.CreateIndex("IX_lesson_viewer_states_tenant_id_student_session_id",
            "lesson_viewer_states", new[] { "tenant_id", "student_session_id" });

        // ── tutor_chat_messages ──
        migrationBuilder.CreateTable(
            name: "tutor_chat_messages",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                student_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                turn_number = table.Column<int>(type: "integer", nullable: false),
                role = table.Column<string>(type: "text", nullable: false),
                modality = table.Column<string>(type: "text", nullable: false),
                language = table.Column<string>(type: "text", nullable: false),
                content_text = table.Column<string>(type: "text", nullable: true),
                voice_capture_reference = table.Column<string>(type: "text", nullable: true),
                voice_playback_reference = table.Column<string>(type: "text", nullable: true),
                ai_request_record_id = table.Column<Guid>(type: "uuid", nullable: true),
                guardrail_final_stage = table.Column<string>(type: "text", nullable: true),
                final_outcome = table.Column<string>(type: "text", nullable: true),
                confidence_signal = table.Column<string>(type: "text", nullable: true),
                evidence_refs = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_tutor_chat_messages", x => x.id));
        migrationBuilder.CreateIndex("IX_tutor_chat_messages_session_turn", "tutor_chat_messages",
            new[] { "student_session_id", "turn_number" }, unique: true);

        // ── voice_captures ──
        migrationBuilder.CreateTable(
            name: "voice_captures",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                student_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                tutor_chat_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                blob_reference = table.Column<string>(type: "text", nullable: false),
                duration_ms = table.Column<int>(type: "integer", nullable: false),
                codec = table.Column<string>(type: "text", nullable: false),
                upload_state = table.Column<string>(type: "text", nullable: false),
                stt_state = table.Column<string>(type: "text", nullable: false),
                transcript_text = table.Column<string>(type: "text", nullable: true),
                stt_adapter_binding_id = table.Column<Guid>(type: "uuid", nullable: true),
                retention_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_voice_captures", x => x.id));
        migrationBuilder.CreateIndex("IX_voice_captures_session", "voice_captures", "student_session_id");

        // ── quiz_sessions ──
        migrationBuilder.CreateTable(
            name: "quiz_sessions",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                student_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                chapter_id = table.Column<Guid>(type: "uuid", nullable: true),
                topic_id = table.Column<Guid>(type: "uuid", nullable: true),
                question_bank_snapshot = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                progress = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                state = table.Column<string>(type: "text", nullable: false),
                started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ended_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_quiz_sessions", x => x.id));
        migrationBuilder.CreateIndex("IX_quiz_sessions_session", "quiz_sessions", "student_session_id");

        // ── mock_test_sessions ──
        migrationBuilder.CreateTable(
            name: "mock_test_sessions",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                student_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                question_bank_snapshot = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                time_limit_seconds = table.Column<int>(type: "integer", nullable: false),
                server_started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                server_deadline_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                progress = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                state = table.Column<string>(type: "text", nullable: false),
                plan_tier_snapshot = table.Column<string>(type: "text", nullable: false),
                final_score = table.Column<double>(type: "double precision", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_mock_test_sessions", x => x.id));
        migrationBuilder.CreateIndex("IX_mock_test_sessions_session", "mock_test_sessions", "student_session_id");

        // ── homework_help_submissions ──
        migrationBuilder.CreateTable(
            name: "homework_help_submissions",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                student_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                input_modality = table.Column<string>(type: "text", nullable: false),
                text_payload = table.Column<string>(type: "text", nullable: true),
                voice_capture_id = table.Column<Guid>(type: "uuid", nullable: true),
                image_blob_reference = table.Column<string>(type: "text", nullable: true),
                image_preprocess_metadata = table.Column<string>(type: "jsonb", nullable: true),
                ocr_adapter_binding_id = table.Column<Guid>(type: "uuid", nullable: true),
                extracted_problem_text = table.Column<string>(type: "text", nullable: true),
                ai_request_record_id = table.Column<Guid>(type: "uuid", nullable: true),
                final_outcome = table.Column<string>(type: "text", nullable: true),
                retention_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_homework_help_submissions", x => x.id));
        migrationBuilder.CreateIndex("IX_homework_help_submissions_session", "homework_help_submissions",
            "student_session_id");

        // ── whiteboard_sessions ──
        migrationBuilder.CreateTable(
            name: "whiteboard_sessions",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                student_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                topic_id = table.Column<Guid>(type: "uuid", nullable: true),
                plan_tier_snapshot = table.Column<string>(type: "text", nullable: false),
                session_mode = table.Column<string>(type: "text", nullable: false),
                step_log = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ended_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                end_reason = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_whiteboard_sessions", x => x.id));
        migrationBuilder.CreateIndex("IX_whiteboard_sessions_session", "whiteboard_sessions", "student_session_id");

        // ── plan_gate_policies ──
        migrationBuilder.CreateTable(
            name: "plan_gate_policies",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                mode = table.Column<string>(type: "text", nullable: false),
                required_plan_tiers = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                subject_scope = table.Column<string>(type: "jsonb", nullable: true),
                grade_scope = table.Column<string>(type: "jsonb", nullable: true),
                enabled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                policy_source = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_plan_gate_policies", x => x.id));
        migrationBuilder.CreateIndex("IX_plan_gate_policies_mode_tenant", "plan_gate_policies",
            new[] { "mode", "tenant_id", "enabled_at" });

        // ── session_events (outbox) ──
        migrationBuilder.CreateTable(
            name: "session_events",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                student_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                event_kind = table.Column<string>(type: "text", nullable: false),
                event_payload = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                curriculum_scope = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                plan_tier_snapshot = table.Column<string>(type: "text", nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                dispatched_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                dispatch_attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                dispatch_state = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_session_events", x => x.id));
        migrationBuilder.CreateIndex("IX_session_events_state_created", "session_events",
            new[] { "dispatch_state", "created_at" });
        migrationBuilder.CreateIndex("IX_session_events_correlation_id", "session_events", "correlation_id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("session_events");
        migrationBuilder.DropTable("plan_gate_policies");
        migrationBuilder.DropTable("whiteboard_sessions");
        migrationBuilder.DropTable("homework_help_submissions");
        migrationBuilder.DropTable("mock_test_sessions");
        migrationBuilder.DropTable("quiz_sessions");
        migrationBuilder.DropTable("voice_captures");
        migrationBuilder.DropTable("tutor_chat_messages");
        migrationBuilder.DropTable("lesson_viewer_states");
        migrationBuilder.DropTable("student_sessions");
        migrationBuilder.DropTable("student_profiles");
    }
}

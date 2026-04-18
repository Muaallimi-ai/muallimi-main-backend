using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muallimi.Infrastructure.Migrations;

/// <summary>
/// Phase 5 — School Management and B2B Administration.
///
/// Adds tables for the school-admin provisioning surface, teacher role binding,
/// class management, exam lifecycle, leaderboard snapshots, announcement dispatch
/// with per-recipient delivery tracking, school reports, licensing enforcement,
/// the Phase 4-fed aggregate view, and the Phase 6-facing downstream event
/// outbox:
///   - school_tenants
///   - school_administrators
///   - teachers
///   - class_groups
///   - class_enrolments
///   - teacher_assignments
///   - roster_imports
///   - exams
///   - exam_questions
///   - exam_assignments
///   - exam_submissions
///   - leaderboard_snapshots
///   - announcements
///   - announcement_deliveries
///   - school_reports
///   - school_licenses
///   - school_aggregate_views
///   - phase5_downstream_events
///
/// Indexes (see data-model.md):
///   - school_administrators UNIQUE(school_tenant_id, user_identity_id)
///   - teachers              UNIQUE(school_tenant_id, user_identity_id)
///   - class_groups          UNIQUE(school_tenant_id, grade, section_label, academic_year)
///   - exam_assignments      UNIQUE(exam_id, class_group_id)
///   - exam_submissions      UNIQUE(exam_id, student_id)
///   - school_licenses       UNIQUE(school_tenant_id)
///   - school_aggregate_views UNIQUE(school_tenant_id, scope_type, scope_id, subject_id)
///   - phase5_downstream_events INDEX(delivery_state, occurred_at)
///   - phase5_downstream_events INDEX(correlation_id)
///
/// This file is the authoritative reference migration. To wire it into EF:
///   1. Copy this file to src/Muallimi.Infrastructure/Migrations/ with a
///      timestamp prefix (e.g. 20260418120000_Phase5_SchoolManagement.cs), or
///      regenerate via `dotnet ef migrations add Phase5_SchoolManagement
///      --project src/Muallimi.Infrastructure`.
///   2. Run `dotnet ef migrations script <previous> Phase5_SchoolManagement`
///      from the repo root to validate the generated SQL.
/// </summary>
public partial class Phase5_SchoolManagement : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "school_tenants",
            columns: table => new
            {
                school_tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                school_name_ar = table.Column<string>(type: "text", nullable: false),
                school_name_en = table.Column<string>(type: "text", nullable: false),
                curriculum_type = table.Column<string>(type: "text", nullable: false),
                grade_range_start = table.Column<int>(type: "integer", nullable: false),
                grade_range_end = table.Column<int>(type: "integer", nullable: false),
                subject_bindings = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                academic_calendar = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                preferred_language = table.Column<string>(type: "text", nullable: false),
                subscription_status = table.Column<string>(type: "text", nullable: false),
                created_by_operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_school_tenants", x => x.school_tenant_id));
        migrationBuilder.CreateIndex("IX_school_tenants_tenant", "school_tenants", "tenant_id");

        migrationBuilder.CreateTable(
            name: "school_administrators",
            columns: table => new
            {
                school_admin_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                school_tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                invitation_email = table.Column<string>(type: "text", nullable: false),
                onboarding_status = table.Column<string>(type: "text", nullable: false),
                terms_accepted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                deactivated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table => table.PrimaryKey("PK_school_administrators", x => x.school_admin_id));
        migrationBuilder.CreateIndex(
            name: "UX_school_admins_school_identity",
            table: "school_administrators",
            columns: new[] { "school_tenant_id", "user_identity_id" },
            unique: true);

        migrationBuilder.CreateTable(
            name: "teachers",
            columns: table => new
            {
                teacher_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                school_tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                display_name_ar = table.Column<string>(type: "text", nullable: false),
                display_name_en = table.Column<string>(type: "text", nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                deactivated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table => table.PrimaryKey("PK_teachers", x => x.teacher_id));
        migrationBuilder.CreateIndex(
            name: "UX_teachers_school_identity",
            table: "teachers",
            columns: new[] { "school_tenant_id", "user_identity_id" },
            unique: true);

        migrationBuilder.CreateTable(
            name: "class_groups",
            columns: table => new
            {
                class_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                school_tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                grade = table.Column<int>(type: "integer", nullable: false),
                section_label = table.Column<string>(type: "text", nullable: false),
                display_name_ar = table.Column<string>(type: "text", nullable: false),
                display_name_en = table.Column<string>(type: "text", nullable: false),
                subject_bindings = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                academic_year = table.Column<string>(type: "text", nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_class_groups", x => x.class_group_id));
        migrationBuilder.CreateIndex(
            name: "UX_class_groups_school_grade_section_year",
            table: "class_groups",
            columns: new[] { "school_tenant_id", "grade", "section_label", "academic_year" },
            unique: true);

        migrationBuilder.CreateTable(
            name: "class_enrolments",
            columns: table => new
            {
                class_enrolment_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                class_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                student_id = table.Column<Guid>(type: "uuid", nullable: false),
                enrolled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                unenrolled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                transfer_to_class_id = table.Column<Guid>(type: "uuid", nullable: true),
                status = table.Column<string>(type: "text", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_class_enrolments", x => x.class_enrolment_id));
        migrationBuilder.CreateIndex(
            name: "IX_class_enrolments_class_student_status",
            table: "class_enrolments",
            columns: new[] { "class_group_id", "student_id", "status" });

        migrationBuilder.CreateTable(
            name: "teacher_assignments",
            columns: table => new
            {
                teacher_assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                teacher_id = table.Column<Guid>(type: "uuid", nullable: false),
                class_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                assigned_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                unassigned_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table => table.PrimaryKey("PK_teacher_assignments", x => x.teacher_assignment_id));
        migrationBuilder.CreateIndex(
            name: "IX_teacher_assignments_scope",
            table: "teacher_assignments",
            columns: new[] { "teacher_id", "class_group_id", "subject_id" });

        migrationBuilder.CreateTable(
            name: "roster_imports",
            columns: table => new
            {
                roster_import_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                school_tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                uploaded_by_admin_id = table.Column<Guid>(type: "uuid", nullable: false),
                source_file_blob_key = table.Column<string>(type: "text", nullable: false),
                original_file_name = table.Column<string>(type: "text", nullable: false),
                total_row_count = table.Column<int>(type: "integer", nullable: false),
                success_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                error_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                skip_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                error_report_blob_key = table.Column<string>(type: "text", nullable: true),
                status = table.Column<string>(type: "text", nullable: false),
                started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_roster_imports", x => x.roster_import_id));
        migrationBuilder.CreateIndex("IX_roster_imports_school_created", "roster_imports", new[] { "school_tenant_id", "created_at" });

        migrationBuilder.CreateTable(
            name: "exams",
            columns: table => new
            {
                exam_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                school_tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                created_by_teacher_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by_admin_id = table.Column<Guid>(type: "uuid", nullable: true),
                title_ar = table.Column<string>(type: "text", nullable: false),
                title_en = table.Column<string>(type: "text", nullable: false),
                subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                grade = table.Column<int>(type: "integer", nullable: false),
                topic_bindings = table.Column<string>(type: "jsonb", nullable: true),
                scheduled_start = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                scheduled_end = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                duration_minutes = table.Column<int>(type: "integer", nullable: true),
                status = table.Column<string>(type: "text", nullable: false),
                total_points = table.Column<decimal>(type: "numeric(9,2)", nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_exams", x => x.exam_id));
        migrationBuilder.CreateIndex("IX_exams_school_status", "exams", new[] { "school_tenant_id", "status" });

        migrationBuilder.CreateTable(
            name: "exam_questions",
            columns: table => new
            {
                exam_question_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                exam_id = table.Column<Guid>(type: "uuid", nullable: false),
                question_source = table.Column<string>(type: "text", nullable: false),
                phase1_content_id = table.Column<Guid>(type: "uuid", nullable: true),
                question_text_ar = table.Column<string>(type: "text", nullable: false),
                question_text_en = table.Column<string>(type: "text", nullable: false),
                question_type = table.Column<string>(type: "text", nullable: false),
                options = table.Column<string>(type: "jsonb", nullable: true),
                correct_answer = table.Column<string>(type: "jsonb", nullable: false),
                points = table.Column<decimal>(type: "numeric(6,2)", nullable: false, defaultValue: 1m),
                display_order = table.Column<int>(type: "integer", nullable: false),
                guardrail_decision_trail_id = table.Column<Guid>(type: "uuid", nullable: true),
            },
            constraints: table => table.PrimaryKey("PK_exam_questions", x => x.exam_question_id));
        migrationBuilder.CreateIndex("IX_exam_questions_order", "exam_questions", new[] { "exam_id", "display_order" });

        migrationBuilder.CreateTable(
            name: "exam_assignments",
            columns: table => new
            {
                exam_assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                exam_id = table.Column<Guid>(type: "uuid", nullable: false),
                class_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                assigned_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_exam_assignments", x => x.exam_assignment_id));
        migrationBuilder.CreateIndex(
            name: "UX_exam_assignments_exam_class",
            table: "exam_assignments",
            columns: new[] { "exam_id", "class_group_id" },
            unique: true);

        migrationBuilder.CreateTable(
            name: "exam_submissions",
            columns: table => new
            {
                exam_submission_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                exam_id = table.Column<Guid>(type: "uuid", nullable: false),
                student_id = table.Column<Guid>(type: "uuid", nullable: false),
                answers = table.Column<string>(type: "jsonb", nullable: false),
                score = table.Column<decimal>(type: "numeric(9,2)", nullable: true),
                max_score = table.Column<decimal>(type: "numeric(9,2)", nullable: false),
                grading_status = table.Column<string>(type: "text", nullable: false),
                started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                graded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                correlation_id = table.Column<string>(type: "text", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_exam_submissions", x => x.exam_submission_id));
        migrationBuilder.CreateIndex(
            name: "UX_exam_submissions_exam_student",
            table: "exam_submissions",
            columns: new[] { "exam_id", "student_id" },
            unique: true);

        migrationBuilder.CreateTable(
            name: "leaderboard_snapshots",
            columns: table => new
            {
                leaderboard_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                school_tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                scope_type = table.Column<string>(type: "text", nullable: false),
                scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                subject_id = table.Column<Guid>(type: "uuid", nullable: true),
                metric = table.Column<string>(type: "text", nullable: false),
                window_start = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                window_end = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                entries = table.Column<string>(type: "jsonb", nullable: false),
                privacy_mode = table.Column<string>(type: "text", nullable: false),
                computed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_leaderboard_snapshots", x => x.leaderboard_snapshot_id));
        migrationBuilder.CreateIndex(
            name: "IX_leaderboard_snapshots_scope",
            table: "leaderboard_snapshots",
            columns: new[] { "school_tenant_id", "scope_type", "scope_id", "metric", "computed_at" });

        migrationBuilder.CreateTable(
            name: "announcements",
            columns: table => new
            {
                announcement_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                school_tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                created_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                target_scope = table.Column<string>(type: "text", nullable: false),
                target_id = table.Column<Guid>(type: "uuid", nullable: true),
                title_ar = table.Column<string>(type: "text", nullable: false),
                title_en = table.Column<string>(type: "text", nullable: false),
                body_ar = table.Column<string>(type: "text", nullable: false),
                body_en = table.Column<string>(type: "text", nullable: false),
                attachments = table.Column<string>(type: "jsonb", nullable: true),
                scheduled_publish_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                status = table.Column<string>(type: "text", nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_announcements", x => x.announcement_id));
        migrationBuilder.CreateIndex("IX_announcements_school_status", "announcements", new[] { "school_tenant_id", "status" });

        migrationBuilder.CreateTable(
            name: "announcement_deliveries",
            columns: table => new
            {
                announcement_delivery_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                announcement_id = table.Column<Guid>(type: "uuid", nullable: false),
                recipient_id = table.Column<Guid>(type: "uuid", nullable: false),
                recipient_role = table.Column<string>(type: "text", nullable: false),
                channel = table.Column<string>(type: "text", nullable: false),
                delivery_status = table.Column<string>(type: "text", nullable: false),
                delivered_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                read_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                correlation_id = table.Column<string>(type: "text", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_announcement_deliveries", x => x.announcement_delivery_id));
        migrationBuilder.CreateIndex("IX_announcement_deliveries_recipient", "announcement_deliveries", new[] { "announcement_id", "recipient_id" });

        migrationBuilder.CreateTable(
            name: "school_reports",
            columns: table => new
            {
                school_report_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                school_tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                generated_by_admin_id = table.Column<Guid>(type: "uuid", nullable: false),
                report_type = table.Column<string>(type: "text", nullable: false),
                grade_filter = table.Column<int>(type: "integer", nullable: true),
                subject_filter = table.Column<Guid>(type: "uuid", nullable: true),
                class_filter = table.Column<Guid>(type: "uuid", nullable: true),
                window_start = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                window_end = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                language = table.Column<string>(type: "text", nullable: false),
                export_blob_key = table.Column<string>(type: "text", nullable: true),
                status = table.Column<string>(type: "text", nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table => table.PrimaryKey("PK_school_reports", x => x.school_report_id));
        migrationBuilder.CreateIndex("IX_school_reports_school_created", "school_reports", new[] { "school_tenant_id", "created_at" });

        migrationBuilder.CreateTable(
            name: "school_licenses",
            columns: table => new
            {
                school_license_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                school_tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                plan_tier = table.Column<string>(type: "text", nullable: false),
                seat_limit = table.Column<int>(type: "integer", nullable: false),
                seats_used = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                feature_gates = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                subscription_start = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                subscription_end = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                is_trial = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                seat_warning_threshold = table.Column<int>(type: "integer", nullable: false, defaultValue: 90),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_school_licenses", x => x.school_license_id));
        migrationBuilder.CreateIndex(
            name: "UX_school_licenses_school_tenant",
            table: "school_licenses",
            column: "school_tenant_id",
            unique: true);

        migrationBuilder.CreateTable(
            name: "school_aggregate_views",
            columns: table => new
            {
                aggregate_view_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                school_tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                scope_type = table.Column<string>(type: "text", nullable: false),
                scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                grade = table.Column<int>(type: "integer", nullable: true),
                subject_id = table.Column<Guid>(type: "uuid", nullable: true),
                active_student_count = table.Column<int>(type: "integer", nullable: false),
                average_mastery = table.Column<decimal>(type: "numeric(6,4)", nullable: false),
                at_risk_count = table.Column<int>(type: "integer", nullable: false),
                active_streak_count = table.Column<int>(type: "integer", nullable: false),
                badges_awarded_count = table.Column<int>(type: "integer", nullable: false),
                last_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                last_event_id = table.Column<Guid>(type: "uuid", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_school_aggregate_views", x => x.aggregate_view_id));
        migrationBuilder.CreateIndex(
            name: "UX_school_aggregate_views_scope",
            table: "school_aggregate_views",
            columns: new[] { "school_tenant_id", "scope_type", "scope_id", "subject_id" },
            unique: true);

        migrationBuilder.CreateTable(
            name: "phase5_downstream_events",
            columns: table => new
            {
                phase5_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                school_tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                event_kind = table.Column<string>(type: "text", nullable: false),
                payload = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                correlation_id = table.Column<string>(type: "text", nullable: false),
                occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                dispatched_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                schema_version = table.Column<string>(type: "text", nullable: false),
                delivery_state = table.Column<string>(type: "text", nullable: false),
                dispatch_attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
            },
            constraints: table => table.PrimaryKey("PK_phase5_downstream_events", x => x.phase5_event_id));
        migrationBuilder.CreateIndex("IX_phase5_downstream_events_delivery", "phase5_downstream_events", new[] { "delivery_state", "occurred_at" });
        migrationBuilder.CreateIndex("IX_phase5_downstream_events_correlation", "phase5_downstream_events", "correlation_id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("phase5_downstream_events");
        migrationBuilder.DropTable("school_aggregate_views");
        migrationBuilder.DropTable("school_licenses");
        migrationBuilder.DropTable("school_reports");
        migrationBuilder.DropTable("announcement_deliveries");
        migrationBuilder.DropTable("announcements");
        migrationBuilder.DropTable("leaderboard_snapshots");
        migrationBuilder.DropTable("exam_submissions");
        migrationBuilder.DropTable("exam_assignments");
        migrationBuilder.DropTable("exam_questions");
        migrationBuilder.DropTable("exams");
        migrationBuilder.DropTable("roster_imports");
        migrationBuilder.DropTable("teacher_assignments");
        migrationBuilder.DropTable("class_enrolments");
        migrationBuilder.DropTable("class_groups");
        migrationBuilder.DropTable("teachers");
        migrationBuilder.DropTable("school_administrators");
        migrationBuilder.DropTable("school_tenants");
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muallimi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase6SaasOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_operations_aggregates",
                columns: table => new
                {
                    aggregate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    phase = table.Column<string>(type: "text", nullable: true),
                    prompt_key = table.Column<string>(type: "text", nullable: true),
                    period_type = table.Column<string>(type: "text", nullable: false),
                    period_start = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    request_count = table.Column<int>(type: "integer", nullable: false),
                    total_input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    total_output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    total_cost_egp = table.Column<decimal>(type: "numeric(12,4)", nullable: false),
                    avg_latency_ms = table.Column<int>(type: "integer", nullable: false),
                    p95_latency_ms = table.Column<int>(type: "integer", nullable: false),
                    p99_latency_ms = table.Column<int>(type: "integer", nullable: false),
                    guardrail_pass_count = table.Column<int>(type: "integer", nullable: false),
                    guardrail_warn_count = table.Column<int>(type: "integer", nullable: false),
                    guardrail_block_count = table.Column<int>(type: "integer", nullable: false),
                    refusal_count = table.Column<int>(type: "integer", nullable: false),
                    computed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_operations_aggregates", x => x.aggregate_id);
                });

            migrationBuilder.CreateTable(
                name: "alert_events",
                columns: table => new
                {
                    alert_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    triggering_value = table.Column<decimal>(type: "numeric(12,4)", nullable: false),
                    threshold_value = table.Column<decimal>(type: "numeric(12,4)", nullable: false),
                    affected_tenants = table.Column<string>(type: "jsonb", nullable: true),
                    sample_correlation_ids = table.Column<string>(type: "jsonb", nullable: true),
                    resolution_status = table.Column<string>(type: "text", nullable: false),
                    resolved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    resolution_notes = table.Column<string>(type: "text", nullable: true),
                    fired_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_events", x => x.alert_event_id);
                });

            migrationBuilder.CreateTable(
                name: "alert_rules",
                columns: table => new
                {
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_name = table.Column<string>(type: "text", nullable: false),
                    metric_type = table.Column<string>(type: "text", nullable: false),
                    threshold_value = table.Column<decimal>(type: "numeric(12,4)", nullable: false),
                    threshold_direction = table.Column<string>(type: "text", nullable: false),
                    evaluation_window_min = table.Column<int>(type: "integer", nullable: false),
                    cooldown_min = table.Column<int>(type: "integer", nullable: false),
                    tenant_scope = table.Column<Guid>(type: "uuid", nullable: true),
                    notification_targets = table.Column<string>(type: "jsonb", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_rules", x => x.rule_id);
                });

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
                    correlation_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_announcement_deliveries", x => x.announcement_delivery_id);
                });

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
                    target_grade = table.Column<int>(type: "integer", nullable: true),
                    title_ar = table.Column<string>(type: "text", nullable: false),
                    title_en = table.Column<string>(type: "text", nullable: false),
                    body_ar = table.Column<string>(type: "text", nullable: false),
                    body_en = table.Column<string>(type: "text", nullable: false),
                    attachments = table.Column<string>(type: "jsonb", nullable: true),
                    scheduled_publish_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_announcements", x => x.announcement_id);
                });

            migrationBuilder.CreateTable(
                name: "audit_entries",
                columns: table => new
                {
                    audit_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_type = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_type = table.Column<string>(type: "text", nullable: true),
                    action_type = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: true),
                    ip_address = table.Column<string>(type: "text", nullable: true),
                    user_agent = table.Column<string>(type: "text", nullable: true),
                    correlation_id = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_entries", x => x.audit_entry_id);
                });

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
                    status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_class_enrolments", x => x.class_enrolment_id);
                });

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
                    subject_bindings = table.Column<string>(type: "jsonb", nullable: false),
                    academic_year = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_class_groups", x => x.class_group_id);
                });

            migrationBuilder.CreateTable(
                name: "data_deletion_requests",
                columns: table => new
                {
                    deletion_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_scope = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    tables_processed = table.Column<string>(type: "jsonb", nullable: true),
                    error_details = table.Column<string>(type: "text", nullable: true),
                    requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    processing_started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    confirmation_sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    correlation_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_deletion_requests", x => x.deletion_request_id);
                });

            migrationBuilder.CreateTable(
                name: "data_retention_policies",
                columns: table => new
                {
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    retention_days = table.Column<int>(type: "integer", nullable: false),
                    anonymisation_rule = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    last_executed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    rows_affected_last_run = table.Column<int>(type: "integer", nullable: true),
                    created_by_operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_retention_policies", x => x.policy_id);
                });

            migrationBuilder.CreateTable(
                name: "exam_assignments",
                columns: table => new
                {
                    exam_assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    exam_id = table.Column<Guid>(type: "uuid", nullable: false),
                    class_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_exam_assignments", x => x.exam_assignment_id);
                });

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
                    points = table.Column<decimal>(type: "numeric(6,2)", nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    guardrail_decision_trail_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_exam_questions", x => x.exam_question_id);
                });

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
                    correlation_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_exam_submissions", x => x.exam_submission_id);
                });

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
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_exams", x => x.exam_id);
                });

            migrationBuilder.CreateTable(
                name: "feature_flags",
                columns: table => new
                {
                    feature_flag_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    flag_name = table.Column<string>(type: "text", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    changed_by_operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    changed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feature_flags", x => x.feature_flag_id);
                });

            migrationBuilder.CreateTable(
                name: "incident_records",
                columns: table => new
                {
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    affected_services = table.Column<string>(type: "jsonb", nullable: false),
                    affected_tenants = table.Column<string>(type: "jsonb", nullable: true),
                    root_cause = table.Column<string>(type: "text", nullable: true),
                    resolution = table.Column<string>(type: "text", nullable: true),
                    runbook_reference = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    opened_by = table.Column<Guid>(type: "uuid", nullable: false),
                    opened_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    mitigated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    resolved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    timeline = table.Column<string>(type: "jsonb", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incident_records", x => x.incident_id);
                });

            migrationBuilder.CreateTable(
                name: "invoices",
                columns: table => new
                {
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_number = table.Column<string>(type: "text", nullable: false),
                    period_start = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    period_end = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    line_items = table.Column<string>(type: "jsonb", nullable: false),
                    subtotal = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    total = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    payment_status = table.Column<string>(type: "text", nullable: false),
                    pdf_blob_key = table.Column<string>(type: "text", nullable: true),
                    issued_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    paid_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoices", x => x.invoice_id);
                });

            migrationBuilder.CreateTable(
                name: "launch_readiness_gates",
                columns: table => new
                {
                    gate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    evaluation_name = table.Column<string>(type: "text", nullable: false),
                    criteria_results = table.Column<string>(type: "jsonb", nullable: false),
                    overall_status = table.Column<string>(type: "text", nullable: false),
                    evaluated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    evaluated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_launch_readiness_gates", x => x.gate_id);
                });

            migrationBuilder.CreateTable(
                name: "leaderboard_configs",
                columns: table => new
                {
                    leaderboard_config_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    school_tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    privacy_mode = table.Column<string>(type: "text", nullable: false),
                    leaderboard_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    per_class_overrides = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leaderboard_configs", x => x.leaderboard_config_id);
                });

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
                    computed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leaderboard_snapshots", x => x.leaderboard_snapshot_id);
                });

            migrationBuilder.CreateTable(
                name: "notification_delivery_receipts",
                columns: table => new
                {
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    notification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    provider_name = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    provider_message_id = table.Column<string>(type: "text", nullable: true),
                    failure_reason = table.Column<string>(type: "text", nullable: true),
                    retry_count = table.Column<int>(type: "integer", nullable: false),
                    next_retry_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    correlation_id = table.Column<string>(type: "text", nullable: false),
                    dispatched_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    delivered_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_delivery_receipts", x => x.receipt_id);
                });

            migrationBuilder.CreateTable(
                name: "notification_provider_bindings",
                columns: table => new
                {
                    binding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    provider_name = table.Column<string>(type: "text", nullable: false),
                    environment = table.Column<string>(type: "text", nullable: false),
                    configuration = table.Column<string>(type: "jsonb", nullable: false),
                    rate_limit_per_minute = table.Column<int>(type: "integer", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_provider_bindings", x => x.binding_id);
                });

            migrationBuilder.CreateTable(
                name: "payment_transactions",
                columns: table => new
                {
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_name = table.Column<string>(type: "text", nullable: false),
                    provider_reference = table.Column<string>(type: "text", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    transaction_type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    failure_reason = table.Column<string>(type: "text", nullable: true),
                    failure_code = table.Column<string>(type: "text", nullable: true),
                    webhook_payload = table.Column<string>(type: "jsonb", nullable: true),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: false),
                    attempted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_transactions", x => x.transaction_id);
                });

            migrationBuilder.CreateTable(
                name: "phase5_downstream_events",
                columns: table => new
                {
                    phase5_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    school_tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_kind = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    dispatched_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    schema_version = table.Column<string>(type: "text", nullable: false),
                    delivery_state = table.Column<string>(type: "text", nullable: false),
                    dispatch_attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_phase5_downstream_events", x => x.phase5_event_id);
                });

            migrationBuilder.CreateTable(
                name: "phase6_ai_operations_metrics",
                columns: table => new
                {
                    metric_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    phase = table.Column<string>(type: "text", nullable: false),
                    prompt_key = table.Column<string>(type: "text", nullable: false),
                    prompt_version = table.Column<string>(type: "text", nullable: false),
                    provider_name = table.Column<string>(type: "text", nullable: false),
                    request_count = table.Column<int>(type: "integer", nullable: false),
                    total_input_tokens = table.Column<int>(type: "integer", nullable: false),
                    total_output_tokens = table.Column<int>(type: "integer", nullable: false),
                    estimated_cost_egp = table.Column<decimal>(type: "numeric(10,4)", nullable: false),
                    latency_ms = table.Column<int>(type: "integer", nullable: false),
                    guardrail_outcome = table.Column<string>(type: "text", nullable: false),
                    confidence_score = table.Column<decimal>(type: "numeric(3,2)", nullable: true),
                    was_refusal = table.Column<bool>(type: "boolean", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_phase6_ai_operations_metrics", x => x.metric_id);
                });

            migrationBuilder.CreateTable(
                name: "phase6_operational_events",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_kind = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    dispatched_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    schema_version = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_phase6_operational_events", x => x.event_id);
                });

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
                    success_count = table.Column<int>(type: "integer", nullable: false),
                    error_count = table.Column<int>(type: "integer", nullable: false),
                    skip_count = table.Column<int>(type: "integer", nullable: false),
                    error_report_blob_key = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roster_imports", x => x.roster_import_id);
                });

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
                    deactivated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_school_administrators", x => x.school_admin_id);
                });

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
                    last_event_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_school_aggregate_views", x => x.aggregate_view_id);
                });

            migrationBuilder.CreateTable(
                name: "school_licenses",
                columns: table => new
                {
                    school_license_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    school_tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_tier = table.Column<string>(type: "text", nullable: false),
                    seat_limit = table.Column<int>(type: "integer", nullable: false),
                    seats_used = table.Column<int>(type: "integer", nullable: false),
                    feature_gates = table.Column<string>(type: "jsonb", nullable: false),
                    subscription_start = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    subscription_end = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    is_trial = table.Column<bool>(type: "boolean", nullable: false),
                    seat_warning_threshold = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_school_licenses", x => x.school_license_id);
                });

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
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_school_reports", x => x.school_report_id);
                });

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
                    subject_bindings = table.Column<string>(type: "jsonb", nullable: false),
                    academic_calendar = table.Column<string>(type: "jsonb", nullable: false),
                    preferred_language = table.Column<string>(type: "text", nullable: false),
                    subscription_status = table.Column<string>(type: "text", nullable: false),
                    created_by_operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_school_tenants", x => x.school_tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "subscription_plans",
                columns: table => new
                {
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_name_ar = table.Column<string>(type: "text", nullable: false),
                    plan_name_en = table.Column<string>(type: "text", nullable: false),
                    plan_type = table.Column<string>(type: "text", nullable: false),
                    tier = table.Column<string>(type: "text", nullable: false),
                    price_egp = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    price_usd = table.Column<decimal>(type: "numeric(10,2)", nullable: true),
                    billing_cycle = table.Column<string>(type: "text", nullable: false),
                    seat_limit = table.Column<int>(type: "integer", nullable: true),
                    feature_entitlements = table.Column<string>(type: "jsonb", nullable: false),
                    usage_limits = table.Column<string>(type: "jsonb", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subscription_plans", x => x.plan_id);
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                columns: table => new
                {
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    current_period_start = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    current_period_end = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    trial_end = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    grace_period_end = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    payment_method_ref = table.Column<string>(type: "text", nullable: true),
                    cancelled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancellation_reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subscriptions", x => x.subscription_id);
                });

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
                    unassigned_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teacher_assignments", x => x.teacher_assignment_id);
                });

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
                    deactivated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teachers", x => x.teacher_id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_health_views",
                columns: table => new
                {
                    tenant_health_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_type = table.Column<string>(type: "text", nullable: false),
                    subscription_status = table.Column<string>(type: "text", nullable: false),
                    plan_tier = table.Column<string>(type: "text", nullable: false),
                    active_student_count = table.Column<int>(type: "integer", nullable: false),
                    monthly_session_count = table.Column<int>(type: "integer", nullable: false),
                    monthly_ai_cost_egp = table.Column<decimal>(type: "numeric(10,4)", nullable: false),
                    storage_usage_mb = table.Column<int>(type: "integer", nullable: false),
                    engagement_score = table.Column<decimal>(type: "numeric(3,2)", nullable: true),
                    at_risk_student_count = table.Column<int>(type: "integer", nullable: false),
                    last_activity_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    computed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_health_views", x => x.tenant_health_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_operations_aggregates_tenant_id_phase_prompt_key_period_~",
                table: "ai_operations_aggregates",
                columns: new[] { "tenant_id", "phase", "prompt_key", "period_type", "period_start" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_alert_events_rule_id_fired_at",
                table: "alert_events",
                columns: new[] { "rule_id", "fired_at" });

            migrationBuilder.CreateIndex(
                name: "IX_announcement_deliveries_announcement_id_recipient_id",
                table: "announcement_deliveries",
                columns: new[] { "announcement_id", "recipient_id" });

            migrationBuilder.CreateIndex(
                name: "IX_announcements_school_tenant_id_status",
                table: "announcements",
                columns: new[] { "school_tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_entries_action_type_occurred_at",
                table: "audit_entries",
                columns: new[] { "action_type", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_entries_actor_id_occurred_at",
                table: "audit_entries",
                columns: new[] { "actor_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_entries_correlation_id",
                table: "audit_entries",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "IX_audit_entries_target_id_occurred_at",
                table: "audit_entries",
                columns: new[] { "target_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_entries_tenant_id_occurred_at",
                table: "audit_entries",
                columns: new[] { "tenant_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_class_enrolments_class_group_id_student_id_status",
                table: "class_enrolments",
                columns: new[] { "class_group_id", "student_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_class_groups_school_tenant_id_grade_section_label_academic_~",
                table: "class_groups",
                columns: new[] { "school_tenant_id", "grade", "section_label", "academic_year" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_data_retention_policies_entity_type",
                table: "data_retention_policies",
                column: "entity_type",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_exam_assignments_exam_id_class_group_id",
                table: "exam_assignments",
                columns: new[] { "exam_id", "class_group_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_exam_questions_exam_id_display_order",
                table: "exam_questions",
                columns: new[] { "exam_id", "display_order" });

            migrationBuilder.CreateIndex(
                name: "IX_exam_submissions_exam_id_student_id",
                table: "exam_submissions",
                columns: new[] { "exam_id", "student_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_exams_school_tenant_id_status",
                table: "exams",
                columns: new[] { "school_tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_feature_flags_tenant_id_flag_name",
                table: "feature_flags",
                columns: new[] { "tenant_id", "flag_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invoices_invoice_number",
                table: "invoices",
                column: "invoice_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_leaderboard_configs_school_tenant_id",
                table: "leaderboard_configs",
                column: "school_tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_leaderboard_snapshots_school_tenant_id_scope_type_scope_id_~",
                table: "leaderboard_snapshots",
                columns: new[] { "school_tenant_id", "scope_type", "scope_id", "metric", "computed_at" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_delivery_receipts_correlation_id",
                table: "notification_delivery_receipts",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "IX_notification_delivery_receipts_notification_id_recipient_id~",
                table: "notification_delivery_receipts",
                columns: new[] { "notification_id", "recipient_id", "channel" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_provider_bindings_channel_environment",
                table: "notification_provider_bindings",
                columns: new[] { "channel", "environment" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_transactions_idempotency_key",
                table: "payment_transactions",
                column: "idempotency_key");

            migrationBuilder.CreateIndex(
                name: "IX_payment_transactions_provider_reference_transaction_type",
                table: "payment_transactions",
                columns: new[] { "provider_reference", "transaction_type" },
                unique: true,
                filter: "provider_reference IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_phase5_downstream_events_correlation_id",
                table: "phase5_downstream_events",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "IX_phase5_downstream_events_delivery_state_occurred_at",
                table: "phase5_downstream_events",
                columns: new[] { "delivery_state", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_phase6_ai_operations_metrics_guardrail_outcome_occurred_at",
                table: "phase6_ai_operations_metrics",
                columns: new[] { "guardrail_outcome", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_phase6_ai_operations_metrics_phase_occurred_at",
                table: "phase6_ai_operations_metrics",
                columns: new[] { "phase", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_phase6_ai_operations_metrics_prompt_key_occurred_at",
                table: "phase6_ai_operations_metrics",
                columns: new[] { "prompt_key", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_phase6_ai_operations_metrics_tenant_id_occurred_at",
                table: "phase6_ai_operations_metrics",
                columns: new[] { "tenant_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_phase6_operational_events_correlation_id",
                table: "phase6_operational_events",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "IX_phase6_operational_events_dispatched_at_occurred_at",
                table: "phase6_operational_events",
                columns: new[] { "dispatched_at", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_roster_imports_school_tenant_id_created_at",
                table: "roster_imports",
                columns: new[] { "school_tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_school_administrators_school_tenant_id_user_identity_id",
                table: "school_administrators",
                columns: new[] { "school_tenant_id", "user_identity_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_school_aggregate_views_school_tenant_id_scope_type_scope_id~",
                table: "school_aggregate_views",
                columns: new[] { "school_tenant_id", "scope_type", "scope_id", "subject_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_school_licenses_school_tenant_id",
                table: "school_licenses",
                column: "school_tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_school_reports_school_tenant_id_created_at",
                table: "school_reports",
                columns: new[] { "school_tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_school_tenants_tenant_id",
                table: "school_tenants",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_subscription_plans_plan_type_tier_billing_cycle",
                table: "subscription_plans",
                columns: new[] { "plan_type", "tier", "billing_cycle" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_tenant_id",
                table: "subscriptions",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_teacher_assignments_teacher_id_class_group_id_subject_id",
                table: "teacher_assignments",
                columns: new[] { "teacher_id", "class_group_id", "subject_id" });

            migrationBuilder.CreateIndex(
                name: "IX_teachers_school_tenant_id_user_identity_id",
                table: "teachers",
                columns: new[] { "school_tenant_id", "user_identity_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_health_views_tenant_id",
                table: "tenant_health_views",
                column: "tenant_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_operations_aggregates");

            migrationBuilder.DropTable(
                name: "alert_events");

            migrationBuilder.DropTable(
                name: "alert_rules");

            migrationBuilder.DropTable(
                name: "announcement_deliveries");

            migrationBuilder.DropTable(
                name: "announcements");

            migrationBuilder.DropTable(
                name: "audit_entries");

            migrationBuilder.DropTable(
                name: "class_enrolments");

            migrationBuilder.DropTable(
                name: "class_groups");

            migrationBuilder.DropTable(
                name: "data_deletion_requests");

            migrationBuilder.DropTable(
                name: "data_retention_policies");

            migrationBuilder.DropTable(
                name: "exam_assignments");

            migrationBuilder.DropTable(
                name: "exam_questions");

            migrationBuilder.DropTable(
                name: "exam_submissions");

            migrationBuilder.DropTable(
                name: "exams");

            migrationBuilder.DropTable(
                name: "feature_flags");

            migrationBuilder.DropTable(
                name: "incident_records");

            migrationBuilder.DropTable(
                name: "invoices");

            migrationBuilder.DropTable(
                name: "launch_readiness_gates");

            migrationBuilder.DropTable(
                name: "leaderboard_configs");

            migrationBuilder.DropTable(
                name: "leaderboard_snapshots");

            migrationBuilder.DropTable(
                name: "notification_delivery_receipts");

            migrationBuilder.DropTable(
                name: "notification_provider_bindings");

            migrationBuilder.DropTable(
                name: "payment_transactions");

            migrationBuilder.DropTable(
                name: "phase5_downstream_events");

            migrationBuilder.DropTable(
                name: "phase6_ai_operations_metrics");

            migrationBuilder.DropTable(
                name: "phase6_operational_events");

            migrationBuilder.DropTable(
                name: "roster_imports");

            migrationBuilder.DropTable(
                name: "school_administrators");

            migrationBuilder.DropTable(
                name: "school_aggregate_views");

            migrationBuilder.DropTable(
                name: "school_licenses");

            migrationBuilder.DropTable(
                name: "school_reports");

            migrationBuilder.DropTable(
                name: "school_tenants");

            migrationBuilder.DropTable(
                name: "subscription_plans");

            migrationBuilder.DropTable(
                name: "subscriptions");

            migrationBuilder.DropTable(
                name: "teacher_assignments");

            migrationBuilder.DropTable(
                name: "teachers");

            migrationBuilder.DropTable(
                name: "tenant_health_views");
        }
    }
}

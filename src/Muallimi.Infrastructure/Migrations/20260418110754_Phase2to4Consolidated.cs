using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muallimi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase2to4Consolidated : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                    per_branch = table.Column<string>(type: "jsonb", nullable: false),
                    prompt_version_distribution = table.Column<string>(type: "jsonb", nullable: false),
                    computed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_operations_metrics", x => x.metric_id);
                });

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
                    stages = table.Column<string>(type: "jsonb", nullable: false),
                    routing_decision = table.Column<string>(type: "jsonb", nullable: false),
                    input_token_count = table.Column<int>(type: "integer", nullable: false),
                    output_token_count = table.Column<int>(type: "integer", nullable: false),
                    latency_ms = table.Column<int>(type: "integer", nullable: false),
                    cache_match_score = table.Column<double>(type: "double precision", nullable: true),
                    final_outcome = table.Column<string>(type: "text", nullable: false),
                    question_text_preview = table.Column<string>(type: "text", nullable: true),
                    prompt_versions_used = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_request_records", x => x.record_id);
                });

            migrationBuilder.CreateTable(
                name: "at_risk_flags",
                columns: table => new
                {
                    at_risk_flag_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    threshold_version = table.Column<string>(type: "text", nullable: false),
                    triggering_evidence = table.Column<string>(type: "jsonb", nullable: false),
                    raised_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    cleared_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    linked_intervention_prompt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    correlation_id = table.Column<string>(type: "text", nullable: false),
                    acknowledged_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    acknowledged_by_parent_profile_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_at_risk_flags", x => x.at_risk_flag_id);
                });

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
                    originating_progress_record_ids = table.Column<string>(type: "jsonb", nullable: false),
                    celebration_shown = table.Column<bool>(type: "boolean", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_badge_awards", x => x.badge_award_id);
                });

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
                    threshold = table.Column<string>(type: "jsonb", nullable: false),
                    retired_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_badge_criteria", x => x.badge_criterion_id);
                });

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
                constraints: table =>
                {
                    table.PrimaryKey("PK_child_links", x => x.child_link_id);
                });

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
                    signal_summary = table.Column<string>(type: "jsonb", nullable: false),
                    rationale_ar = table.Column<string>(type: "text", nullable: false),
                    rationale_en = table.Column<string>(type: "text", nullable: false),
                    suggested_next_step = table.Column<string>(type: "jsonb", nullable: false),
                    guardrail_decision_trail_id = table.Column<Guid>(type: "uuid", nullable: false),
                    computed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    valid_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_focus_areas", x => x.focus_area_id);
                });

            migrationBuilder.CreateTable(
                name: "guardrail_decision_trails",
                columns: table => new
                {
                    guardrail_decision_trail_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artefact_kind = table.Column<string>(type: "text", nullable: false),
                    artefact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    prompt_key = table.Column<string>(type: "text", nullable: false),
                    chain_output = table.Column<string>(type: "jsonb", nullable: false),
                    final_stage = table.Column<string>(type: "text", nullable: false),
                    language = table.Column<string>(type: "text", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: false),
                    captured_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guardrail_decision_trails", x => x.guardrail_decision_trail_id);
                });

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
                constraints: table =>
                {
                    table.PrimaryKey("PK_homework_help_submissions", x => x.id);
                });

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
                    next_step = table.Column<string>(type: "jsonb", nullable: false),
                    guardrail_decision_trail_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_intervention_prompts", x => x.intervention_prompt_id);
                });

            migrationBuilder.CreateTable(
                name: "lesson_viewer_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lesson_id = table.Column<Guid>(type: "uuid", nullable: false),
                    viewer_position = table.Column<string>(type: "jsonb", nullable: false),
                    playback_state = table.Column<string>(type: "text", nullable: false),
                    teacher_voice_profile_id = table.Column<string>(type: "text", nullable: true),
                    captions_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    rate = table.Column<double>(type: "double precision", nullable: false),
                    last_interaction_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lesson_viewer_states", x => x.id);
                });

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
                constraints: table =>
                {
                    table.PrimaryKey("PK_mastery_states", x => x.mastery_state_id);
                });

            migrationBuilder.CreateTable(
                name: "mock_test_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    question_bank_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    time_limit_seconds = table.Column<int>(type: "integer", nullable: false),
                    server_started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    server_deadline_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    progress = table.Column<string>(type: "jsonb", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    plan_tier_snapshot = table.Column<string>(type: "text", nullable: false),
                    final_score = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mock_test_sessions", x => x.id);
                });

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
                constraints: table =>
                {
                    table.PrimaryKey("PK_operator_impersonation_audits", x => x.operator_impersonation_audit_id);
                });

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
                constraints: table =>
                {
                    table.PrimaryKey("PK_parent_notifications", x => x.parent_notification_id);
                });

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
                    notification_channels = table.Column<string>(type: "jsonb", nullable: false),
                    quiet_hours = table.Column<string>(type: "jsonb", nullable: false),
                    per_child_overrides = table.Column<string>(type: "jsonb", nullable: false),
                    consent_state = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_parent_profiles", x => x.parent_profile_id);
                });

            migrationBuilder.CreateTable(
                name: "phase4_downstream_events",
                columns: table => new
                {
                    phase4_downstream_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_kind = table.Column<string>(type: "text", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "jsonb", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    dispatched_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    delivery_state = table.Column<string>(type: "text", nullable: false),
                    dispatch_attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_phase4_downstream_events", x => x.phase4_downstream_event_id);
                });

            migrationBuilder.CreateTable(
                name: "plan_gate_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    mode = table.Column<string>(type: "text", nullable: false),
                    required_plan_tiers = table.Column<string>(type: "jsonb", nullable: false),
                    subject_scope = table.Column<string>(type: "jsonb", nullable: true),
                    grade_scope = table.Column<string>(type: "jsonb", nullable: true),
                    enabled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    policy_source = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plan_gate_policies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "progress_ingestion_dead_letters",
                columns: table => new
                {
                    dead_letter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    student_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_event_id = table.Column<string>(type: "text", nullable: false),
                    event_kind = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    envelope = table.Column<string>(type: "jsonb", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_progress_ingestion_dead_letters", x => x.dead_letter_id);
                });

            migrationBuilder.CreateTable(
                name: "progress_records",
                columns: table => new
                {
                    progress_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_event_id = table.Column<string>(type: "text", nullable: false),
                    event_kind = table.Column<string>(type: "text", nullable: false),
                    curriculum_scope = table.Column<string>(type: "jsonb", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ingested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_progress_records", x => x.progress_record_id);
                });

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
                constraints: table =>
                {
                    table.PrimaryKey("PK_prompt_audit_entries", x => x.entry_id);
                });

            migrationBuilder.CreateTable(
                name: "prompt_versions",
                columns: table => new
                {
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    prompt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    declared_variables = table.Column<string>(type: "jsonb", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prompt_versions", x => x.version_id);
                });

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
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prompts", x => x.prompt_id);
                });

            migrationBuilder.CreateTable(
                name: "provider_adapter_bindings",
                columns: table => new
                {
                    binding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    capability = table.Column<string>(type: "text", nullable: false),
                    environment = table.Column<string>(type: "text", nullable: false),
                    curriculum_scope = table.Column<string>(type: "text", nullable: true),
                    provider_identifier = table.Column<string>(type: "text", nullable: false),
                    provider_configuration_ref = table.Column<string>(type: "text", nullable: true),
                    fallback_chain = table.Column<string>(type: "jsonb", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    promotion_block_flag = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_adapter_bindings", x => x.binding_id);
                });

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
                    question_bank_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    progress = table.Column<string>(type: "jsonb", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quiz_sessions", x => x.id);
                });

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
                    regressions = table.Column<string>(type: "jsonb", nullable: false),
                    promotion_block_flag = table.Column<bool>(type: "boolean", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_red_team_evaluation_results", x => x.result_id);
                });

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
                constraints: table =>
                {
                    table.PrimaryKey("PK_red_team_scenario_sets", x => x.set_id);
                });

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
                constraints: table =>
                {
                    table.PrimaryKey("PK_refusal_events", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "session_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_kind = table.Column<string>(type: "text", nullable: false),
                    event_payload = table.Column<string>(type: "jsonb", nullable: false),
                    curriculum_scope = table.Column<string>(type: "jsonb", nullable: false),
                    plan_tier_snapshot = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    dispatched_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    dispatch_attempts = table.Column<int>(type: "integer", nullable: false),
                    dispatch_state = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_events", x => x.id);
                });

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
                    reset_history = table.Column<string>(type: "jsonb", nullable: false),
                    last_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_streak_states", x => x.streak_state_id);
                });

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
                    subjects_enrolled = table.Column<string>(type: "jsonb", nullable: false),
                    consent_state = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_student_profiles", x => x.id);
                });

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
                constraints: table =>
                {
                    table.PrimaryKey("PK_student_sessions", x => x.id);
                });

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
                    evidence_refs = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tutor_chat_messages", x => x.id);
                });

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
                constraints: table =>
                {
                    table.PrimaryKey("PK_voice_captures", x => x.id);
                });

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
                    mastery_deltas = table.Column<string>(type: "jsonb", nullable: false),
                    top_focus_areas = table.Column<string>(type: "jsonb", nullable: false),
                    awarded_badges = table.Column<string>(type: "jsonb", nullable: false),
                    summary_ar = table.Column<string>(type: "text", nullable: false),
                    summary_en = table.Column<string>(type: "text", nullable: false),
                    guardrail_decision_trail_id = table.Column<Guid>(type: "uuid", nullable: false),
                    evidence_refs = table.Column<string>(type: "jsonb", nullable: false),
                    share_token_hash = table.Column<string>(type: "text", nullable: true),
                    correlation_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_weekly_reports", x => x.weekly_report_id);
                });

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
                    step_log = table.Column<string>(type: "jsonb", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    end_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_whiteboard_sessions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_records_correlation_id_session_id_curriculum_typ~",
                table: "ai_request_records",
                columns: new[] { "correlation_id", "session_id", "curriculum_type", "final_outcome" });

            migrationBuilder.CreateIndex(
                name: "IX_at_risk_flags_tenant_id_student_id_cleared_at",
                table: "at_risk_flags",
                columns: new[] { "tenant_id", "student_id", "cleared_at" });

            migrationBuilder.CreateIndex(
                name: "IX_badge_awards_tenant_id_student_id_badge_criterion_id_badge_~",
                table: "badge_awards",
                columns: new[] { "tenant_id", "student_id", "badge_criterion_id", "badge_criterion_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_badge_criteria_badge_key_version",
                table: "badge_criteria",
                columns: new[] { "badge_key", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_child_links_tenant_id_parent_profile_id_student_id",
                table: "child_links",
                columns: new[] { "tenant_id", "parent_profile_id", "student_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_focus_areas_tenant_id_student_id",
                table: "focus_areas",
                columns: new[] { "tenant_id", "student_id" });

            migrationBuilder.CreateIndex(
                name: "IX_guardrail_decision_trails_artefact_kind_artefact_id",
                table: "guardrail_decision_trails",
                columns: new[] { "artefact_kind", "artefact_id" });

            migrationBuilder.CreateIndex(
                name: "IX_homework_help_submissions_student_session_id",
                table: "homework_help_submissions",
                column: "student_session_id");

            migrationBuilder.CreateIndex(
                name: "IX_intervention_prompts_tenant_id_student_id_created_at",
                table: "intervention_prompts",
                columns: new[] { "tenant_id", "student_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_lesson_viewer_states_tenant_id_student_session_id",
                table: "lesson_viewer_states",
                columns: new[] { "tenant_id", "student_session_id" });

            migrationBuilder.CreateIndex(
                name: "IX_mastery_states_tenant_id_student_id_subject_id_topic_id_cal~",
                table: "mastery_states",
                columns: new[] { "tenant_id", "student_id", "subject_id", "topic_id", "calculation_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mock_test_sessions_student_session_id",
                table: "mock_test_sessions",
                column: "student_session_id");

            migrationBuilder.CreateIndex(
                name: "IX_operator_impersonation_audits_tenant_id_target_parent_profi~",
                table: "operator_impersonation_audits",
                columns: new[] { "tenant_id", "target_parent_profile_id", "viewed_at" });

            migrationBuilder.CreateIndex(
                name: "IX_parent_notifications_delivery_state_created_at",
                table: "parent_notifications",
                columns: new[] { "delivery_state", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_parent_profiles_tenant_id_identity_id",
                table: "parent_profiles",
                columns: new[] { "tenant_id", "identity_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_phase4_downstream_events_correlation_id",
                table: "phase4_downstream_events",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "IX_phase4_downstream_events_delivery_state_occurred_at",
                table: "phase4_downstream_events",
                columns: new[] { "delivery_state", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_plan_gate_policies_mode_tenant_id_enabled_at",
                table: "plan_gate_policies",
                columns: new[] { "mode", "tenant_id", "enabled_at" });

            migrationBuilder.CreateIndex(
                name: "IX_progress_ingestion_dead_letters_reason",
                table: "progress_ingestion_dead_letters",
                column: "reason");

            migrationBuilder.CreateIndex(
                name: "IX_progress_ingestion_dead_letters_tenant_id_recorded_at",
                table: "progress_ingestion_dead_letters",
                columns: new[] { "tenant_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "IX_progress_records_correlation_id",
                table: "progress_records",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "IX_progress_records_tenant_id_source_event_id",
                table: "progress_records",
                columns: new[] { "tenant_id", "source_event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_progress_records_tenant_id_student_id",
                table: "progress_records",
                columns: new[] { "tenant_id", "student_id" });

            migrationBuilder.CreateIndex(
                name: "IX_prompt_audit_entries_prompt_id",
                table: "prompt_audit_entries",
                column: "prompt_id");

            migrationBuilder.CreateIndex(
                name: "IX_prompt_versions_prompt_id_version_number",
                table: "prompt_versions",
                columns: new[] { "prompt_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_prompts_name",
                table: "prompts",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_adapter_bindings_capability_environment_curriculu~1",
                table: "provider_adapter_bindings",
                columns: new[] { "capability", "environment", "curriculum_scope", "active" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_adapter_bindings_capability_environment_curriculum~",
                table: "provider_adapter_bindings",
                columns: new[] { "capability", "environment", "curriculum_scope" },
                unique: true,
                filter: "active = true");

            migrationBuilder.CreateIndex(
                name: "IX_quiz_sessions_student_session_id",
                table: "quiz_sessions",
                column: "student_session_id");

            migrationBuilder.CreateIndex(
                name: "IX_refusal_events_record_id",
                table: "refusal_events",
                column: "record_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_events_correlation_id",
                table: "session_events",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_events_dispatch_state_created_at",
                table: "session_events",
                columns: new[] { "dispatch_state", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_streak_states_tenant_id_student_id",
                table: "streak_states",
                columns: new[] { "tenant_id", "student_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_student_profiles_tenant_id",
                table: "student_profiles",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_student_sessions_correlation_id",
                table: "student_sessions",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "IX_student_sessions_tenant_id_student_profile_id",
                table: "student_sessions",
                columns: new[] { "tenant_id", "student_profile_id" });

            migrationBuilder.CreateIndex(
                name: "IX_tutor_chat_messages_student_session_id_turn_number",
                table: "tutor_chat_messages",
                columns: new[] { "student_session_id", "turn_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_voice_captures_student_session_id",
                table: "voice_captures",
                column: "student_session_id");

            migrationBuilder.CreateIndex(
                name: "IX_weekly_reports_tenant_id_student_id_window_start_window_end",
                table: "weekly_reports",
                columns: new[] { "tenant_id", "student_id", "window_start", "window_end" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_whiteboard_sessions_student_session_id",
                table: "whiteboard_sessions",
                column: "student_session_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_operations_metrics");

            migrationBuilder.DropTable(
                name: "ai_request_records");

            migrationBuilder.DropTable(
                name: "at_risk_flags");

            migrationBuilder.DropTable(
                name: "badge_awards");

            migrationBuilder.DropTable(
                name: "badge_criteria");

            migrationBuilder.DropTable(
                name: "child_links");

            migrationBuilder.DropTable(
                name: "focus_areas");

            migrationBuilder.DropTable(
                name: "guardrail_decision_trails");

            migrationBuilder.DropTable(
                name: "homework_help_submissions");

            migrationBuilder.DropTable(
                name: "intervention_prompts");

            migrationBuilder.DropTable(
                name: "lesson_viewer_states");

            migrationBuilder.DropTable(
                name: "mastery_states");

            migrationBuilder.DropTable(
                name: "mock_test_sessions");

            migrationBuilder.DropTable(
                name: "operator_impersonation_audits");

            migrationBuilder.DropTable(
                name: "parent_notifications");

            migrationBuilder.DropTable(
                name: "parent_profiles");

            migrationBuilder.DropTable(
                name: "phase4_downstream_events");

            migrationBuilder.DropTable(
                name: "plan_gate_policies");

            migrationBuilder.DropTable(
                name: "progress_ingestion_dead_letters");

            migrationBuilder.DropTable(
                name: "progress_records");

            migrationBuilder.DropTable(
                name: "prompt_audit_entries");

            migrationBuilder.DropTable(
                name: "prompt_versions");

            migrationBuilder.DropTable(
                name: "prompts");

            migrationBuilder.DropTable(
                name: "provider_adapter_bindings");

            migrationBuilder.DropTable(
                name: "quiz_sessions");

            migrationBuilder.DropTable(
                name: "red_team_evaluation_results");

            migrationBuilder.DropTable(
                name: "red_team_scenario_sets");

            migrationBuilder.DropTable(
                name: "refusal_events");

            migrationBuilder.DropTable(
                name: "session_events");

            migrationBuilder.DropTable(
                name: "streak_states");

            migrationBuilder.DropTable(
                name: "student_profiles");

            migrationBuilder.DropTable(
                name: "student_sessions");

            migrationBuilder.DropTable(
                name: "tutor_chat_messages");

            migrationBuilder.DropTable(
                name: "voice_captures");

            migrationBuilder.DropTable(
                name: "weekly_reports");

            migrationBuilder.DropTable(
                name: "whiteboard_sessions");
        }
    }
}

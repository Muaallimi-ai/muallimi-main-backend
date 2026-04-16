using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace Muallimi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "auto_validation_results",
                columns: table => new
                {
                    result_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    checks = table.Column<string>(type: "jsonb", nullable: false),
                    grounding_evidence = table.Column<string>(type: "jsonb", nullable: false),
                    arabic_quality = table.Column<string>(type: "jsonb", nullable: true),
                    rendering = table.Column<string>(type: "jsonb", nullable: true),
                    narration_sync = table.Column<string>(type: "jsonb", nullable: true),
                    accessibility = table.Column<string>(type: "jsonb", nullable: true),
                    alignment = table.Column<string>(type: "jsonb", nullable: true),
                    decision = table.Column<string>(type: "text", nullable: false),
                    validated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auto_validation_results", x => x.result_id);
                });

            migrationBuilder.CreateTable(
                name: "change_log_entries",
                columns: table => new
                {
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lesson_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_change_log_entries", x => x.entry_id);
                });

            migrationBuilder.CreateTable(
                name: "coverage_statuses",
                columns: table => new
                {
                    lesson_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_type = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    queue_age = table.Column<long>(type: "bigint", nullable: true),
                    owner = table.Column<string>(type: "text", nullable: true),
                    last_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_coverage_statuses", x => new { x.lesson_id, x.asset_type });
                });

            migrationBuilder.CreateTable(
                name: "curriculum_sources",
                columns: table => new
                {
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    curriculum_type = table.Column<string>(type: "text", nullable: false),
                    grade = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    academic_year = table.Column<string>(type: "text", nullable: false),
                    tutor_language = table.Column<string>(type: "text", nullable: false),
                    file_format = table.Column<string>(type: "text", nullable: false),
                    storage_key = table.Column<string>(type: "text", nullable: false),
                    upload_actor = table.Column<string>(type: "text", nullable: false),
                    uploaded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_curriculum_sources", x => x.source_id);
                });

            migrationBuilder.CreateTable(
                name: "format_decisions",
                columns: table => new
                {
                    decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lesson_id = table.Column<Guid>(type: "uuid", nullable: false),
                    selected_format = table.Column<string>(type: "text", nullable: false),
                    rule_triggered = table.Column<string>(type: "text", nullable: false),
                    llm_refinement = table.Column<string>(type: "text", nullable: true),
                    overridden_by = table.Column<string>(type: "text", nullable: true),
                    overridden_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_format_decisions", x => x.decision_id);
                });

            migrationBuilder.CreateTable(
                name: "generated_assets",
                columns: table => new
                {
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lesson_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_type = table.Column<string>(type: "text", nullable: false),
                    visual_format = table.Column<string>(type: "text", nullable: true),
                    storage_key = table.Column<string>(type: "text", nullable: false),
                    transcript = table.Column<string>(type: "text", nullable: true),
                    language = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    produced_by = table.Column<string>(type: "text", nullable: false),
                    produced_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    cost = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generated_assets", x => x.asset_id);
                });

            migrationBuilder.CreateTable(
                name: "generation_jobs",
                columns: table => new
                {
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lesson_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "jsonb", nullable: false),
                    stages = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: true),
                    cost_summary = table.Column<string>(type: "jsonb", nullable: true),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    error_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generation_jobs", x => x.job_id);
                });

            migrationBuilder.CreateTable(
                name: "ingestion_jobs",
                columns: table => new
                {
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    stages = table.Column<string>(type: "jsonb", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    correlation_id = table.Column<string>(type: "text", nullable: true),
                    error_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingestion_jobs", x => x.job_id);
                });

            migrationBuilder.CreateTable(
                name: "published_assets",
                columns: table => new
                {
                    published_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lesson_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_type = table.Column<string>(type: "text", nullable: false),
                    visual_format = table.Column<string>(type: "text", nullable: true),
                    runtime_url = table.Column<string>(type: "text", nullable: false),
                    approved_by_admin = table.Column<string>(type: "text", nullable: false),
                    approved_by_expert = table.Column<string>(type: "text", nullable: false),
                    approved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_published_assets", x => x.published_id);
                });

            migrationBuilder.CreateTable(
                name: "qa_cache_entries",
                columns: table => new
                {
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    curriculum_type = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    topic = table.Column<string>(type: "text", nullable: false),
                    grade = table.Column<string>(type: "text", nullable: false),
                    tutor_language = table.Column<string>(type: "text", nullable: false),
                    question_text = table.Column<string>(type: "text", nullable: false),
                    question_embedding = table.Column<Vector>(type: "vector(1536)", nullable: true),
                    answer_text = table.Column<string>(type: "text", nullable: false),
                    source_chunk_ids = table.Column<string>(type: "jsonb", nullable: false),
                    model_version = table.Column<string>(type: "text", nullable: false),
                    validation_status = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    last_reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_qa_cache_entries", x => x.entry_id);
                });

            migrationBuilder.CreateTable(
                name: "review_assignments",
                columns: table => new
                {
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tier = table.Column<string>(type: "text", nullable: false),
                    assigned_to = table.Column<string>(type: "text", nullable: false),
                    assigned_by = table.Column<string>(type: "text", nullable: false),
                    assigned_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sla_due_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_assignments", x => x.assignment_id);
                });

            migrationBuilder.CreateTable(
                name: "review_decisions",
                columns: table => new
                {
                    decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tier = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    scope = table.Column<string>(type: "text", nullable: true),
                    fix_instruction = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_decisions", x => x.decision_id);
                });

            migrationBuilder.CreateTable(
                name: "curriculum_structures",
                columns: table => new
                {
                    structure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    nodes = table.Column<string>(type: "jsonb", nullable: false),
                    extracted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_curriculum_structures", x => x.structure_id);
                    table.ForeignKey(
                        name: "FK_curriculum_structures_curriculum_sources_source_id",
                        column: x => x.source_id,
                        principalTable: "curriculum_sources",
                        principalColumn: "source_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lessons",
                columns: table => new
                {
                    lesson_id = table.Column<Guid>(type: "uuid", nullable: false),
                    structure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    curriculum_type = table.Column<string>(type: "text", nullable: false),
                    grade = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    tutor_language = table.Column<string>(type: "text", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_change_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lessons", x => x.lesson_id);
                    table.ForeignKey(
                        name: "FK_lessons_curriculum_structures_structure_id",
                        column: x => x.structure_id,
                        principalTable: "curriculum_structures",
                        principalColumn: "structure_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "content_chunks",
                columns: table => new
                {
                    chunk_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lesson_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    math_blocks = table.Column<string>(type: "jsonb", nullable: false),
                    token_count = table.Column<int>(type: "integer", nullable: false),
                    overlap_with_previous = table.Column<int>(type: "integer", nullable: false),
                    source_refs = table.Column<string>(type: "jsonb", nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: false),
                    embedding = table.Column<Vector>(type: "vector(1536)", nullable: true),
                    embedding_model_version = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_chunks", x => x.chunk_id);
                    table.ForeignKey(
                        name: "FK_content_chunks_lessons_lesson_id",
                        column: x => x.lesson_id,
                        principalTable: "lessons",
                        principalColumn: "lesson_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_content_chunks_lesson_id",
                table: "content_chunks",
                column: "lesson_id");

            migrationBuilder.CreateIndex(
                name: "IX_curriculum_structures_source_id",
                table: "curriculum_structures",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "IX_lessons_structure_id",
                table: "lessons",
                column: "structure_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auto_validation_results");

            migrationBuilder.DropTable(
                name: "change_log_entries");

            migrationBuilder.DropTable(
                name: "content_chunks");

            migrationBuilder.DropTable(
                name: "coverage_statuses");

            migrationBuilder.DropTable(
                name: "format_decisions");

            migrationBuilder.DropTable(
                name: "generated_assets");

            migrationBuilder.DropTable(
                name: "generation_jobs");

            migrationBuilder.DropTable(
                name: "ingestion_jobs");

            migrationBuilder.DropTable(
                name: "published_assets");

            migrationBuilder.DropTable(
                name: "qa_cache_entries");

            migrationBuilder.DropTable(
                name: "review_assignments");

            migrationBuilder.DropTable(
                name: "review_decisions");

            migrationBuilder.DropTable(
                name: "lessons");

            migrationBuilder.DropTable(
                name: "curriculum_structures");

            migrationBuilder.DropTable(
                name: "curriculum_sources");
        }
    }
}

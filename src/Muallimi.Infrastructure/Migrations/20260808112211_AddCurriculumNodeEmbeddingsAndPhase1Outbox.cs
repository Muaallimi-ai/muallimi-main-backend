using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace Muallimi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCurriculumNodeEmbeddingsAndPhase1Outbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "curriculum_node_embeddings",
                columns: table => new
                {
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    retrieval_class = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    embed_body_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    provider_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    model_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    dim = table.Column<int>(type: "integer", nullable: false),
                    voyage_embedding = table.Column<Vector>(type: "vector(1024)", nullable: true),
                    openai_embedding = table.Column<Vector>(type: "vector(3072)", nullable: true),
                    embedded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_curriculum_node_embeddings", x => x.node_id);
                });

            migrationBuilder.CreateTable(
                name: "phase1_downstream_events",
                columns: table => new
                {
                    phase1_downstream_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    dispatched_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    delivery_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    dispatch_attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_phase1_downstream_events", x => x.phase1_downstream_event_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_curriculum_node_embeddings_source_id",
                table: "curriculum_node_embeddings",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "ix_phase1_downstream_events_state_time",
                table: "phase1_downstream_events",
                columns: new[] { "delivery_state", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "curriculum_node_embeddings");

            migrationBuilder.DropTable(
                name: "phase1_downstream_events");
        }
    }
}

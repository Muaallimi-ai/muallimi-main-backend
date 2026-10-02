using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muallimi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCurriculumNodeContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "curriculum_node_content_reports",
                columns: table => new
                {
                    report_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    comment = table.Column<string>(type: "text", nullable: false),
                    reported_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    reported_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_curriculum_node_content_reports", x => x.report_id);
                });

            migrationBuilder.CreateTable(
                name: "curriculum_node_contents",
                columns: table => new
                {
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    markdown = table.Column<string>(type: "text", nullable: true),
                    image_description = table.Column<string>(type: "text", nullable: true),
                    content_hash = table.Column<string>(type: "text", nullable: true),
                    error_reason = table.Column<string>(type: "text", nullable: true),
                    requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    fetched_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    model_version = table.Column<string>(type: "text", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: true),
                    is_approved = table.Column<bool>(type: "boolean", nullable: false),
                    approved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_curriculum_node_contents", x => x.node_id);
                    table.ForeignKey(
                        name: "FK_curriculum_node_contents_curriculum_sources_source_id",
                        column: x => x.source_id,
                        principalTable: "curriculum_sources",
                        principalColumn: "source_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_curriculum_node_content_reports_node_id",
                table: "curriculum_node_content_reports",
                column: "node_id");

            migrationBuilder.CreateIndex(
                name: "ix_curriculum_node_content_reports_source_id",
                table: "curriculum_node_content_reports",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "ix_curriculum_node_contents_source_id",
                table: "curriculum_node_contents",
                column: "source_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "curriculum_node_content_reports");

            migrationBuilder.DropTable(
                name: "curriculum_node_contents");
        }
    }
}

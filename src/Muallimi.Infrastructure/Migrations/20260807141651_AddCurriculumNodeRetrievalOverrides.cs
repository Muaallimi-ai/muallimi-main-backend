using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muallimi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCurriculumNodeRetrievalOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "curriculum_node_retrieval_overrides",
                columns: table => new
                {
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    retrieval_class = table.Column<string>(type: "text", nullable: false),
                    overridden_by_user_id = table.Column<string>(type: "text", nullable: false),
                    overridden_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_curriculum_node_retrieval_overrides", x => x.node_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_curriculum_node_retrieval_overrides_source_id",
                table: "curriculum_node_retrieval_overrides",
                column: "source_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "curriculum_node_retrieval_overrides");
        }
    }
}

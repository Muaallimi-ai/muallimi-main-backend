using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muallimi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPromptRegistryStamping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "prompt_key",
                table: "curriculum_sources",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "prompt_sha",
                table: "curriculum_sources",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "prompt_version",
                table: "curriculum_sources",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "prompt_key",
                table: "curriculum_sources");

            migrationBuilder.DropColumn(
                name: "prompt_sha",
                table: "curriculum_sources");

            migrationBuilder.DropColumn(
                name: "prompt_version",
                table: "curriculum_sources");
        }
    }
}

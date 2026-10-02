using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace Muallimi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalEmbeddingColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Vector>(
                name: "local_embedding",
                table: "curriculum_node_embeddings",
                type: "vector(384)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "local_embedding",
                table: "curriculum_node_embeddings");
        }
    }
}

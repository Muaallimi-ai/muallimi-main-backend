using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muallimi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ModelDriftSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_incident_records_correlation_id",
                table: "incident_records",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "IX_incident_records_severity_opened_at",
                table: "incident_records",
                columns: new[] { "severity", "opened_at" });

            migrationBuilder.CreateIndex(
                name: "IX_incident_records_status_opened_at",
                table: "incident_records",
                columns: new[] { "status", "opened_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_incident_records_correlation_id",
                table: "incident_records");

            migrationBuilder.DropIndex(
                name: "IX_incident_records_severity_opened_at",
                table: "incident_records");

            migrationBuilder.DropIndex(
                name: "IX_incident_records_status_opened_at",
                table: "incident_records");
        }
    }
}

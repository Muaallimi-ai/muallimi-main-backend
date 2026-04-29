using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muallimi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCredentialConcurrencyAndAgeTransition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "age_transition_notified_at",
                table: "identity_users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "password_hash_version",
                table: "identity_users",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "age_transition_notified_at",
                table: "identity_users");

            migrationBuilder.DropColumn(
                name: "password_hash_version",
                table: "identity_users");
        }
    }
}

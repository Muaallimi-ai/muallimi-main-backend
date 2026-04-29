using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muallimi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingParentResetNotice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "pending_parent_reset_notice_at",
                table: "identity_users",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pending_parent_reset_notice_at",
                table: "identity_users");
        }
    }
}

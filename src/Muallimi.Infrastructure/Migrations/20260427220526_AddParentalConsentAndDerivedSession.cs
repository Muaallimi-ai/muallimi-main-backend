using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muallimi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddParentalConsentAndDerivedSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "derived_from_session_id",
                table: "identity_user_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "parental_consents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    child_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consented_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    is_legacy_assumed = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_parental_consents", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_identity_user_sessions_derived_from_session_id",
                table: "identity_user_sessions",
                column: "derived_from_session_id");

            migrationBuilder.CreateIndex(
                name: "IX_parental_consents_parent_user_id_child_user_id",
                table: "parental_consents",
                columns: new[] { "parent_user_id", "child_user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_parental_consents_tenant_id",
                table: "parental_consents",
                column: "tenant_id");

            // Backfill legacy consent rows for every Managed (child) user that
            // already exists. Decision #10 + section 3 of the redesign:
            // existing parents are NOT forced through a consent gate; their
            // children get a row marked is_legacy_assumed = true.
            migrationBuilder.Sql(@"
                INSERT INTO parental_consents (
                    id, tenant_id, parent_user_id, child_user_id,
                    consented_at, ip_address, is_legacy_assumed, created_at)
                SELECT
                    gen_random_uuid(),
                    c.tenant_id,
                    c.managed_by_user_id,
                    c.id,
                    NOW(),
                    NULL,
                    TRUE,
                    NOW()
                FROM identity_users c
                WHERE c.account_type = 2
                  AND c.managed_by_user_id IS NOT NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM parental_consents p
                    WHERE p.parent_user_id = c.managed_by_user_id
                      AND p.child_user_id = c.id
                  );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "parental_consents");

            migrationBuilder.DropIndex(
                name: "IX_identity_user_sessions_derived_from_session_id",
                table: "identity_user_sessions");

            migrationBuilder.DropColumn(
                name: "derived_from_session_id",
                table: "identity_user_sessions");
        }
    }
}

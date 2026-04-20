using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muallimi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase9Identity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "user_id",
                table: "teachers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "user_id",
                table: "student_profiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "user_id",
                table: "school_administrators",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "user_id",
                table: "parent_profiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "identity_email_verification_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    issued_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_email_verification_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "identity_impersonation_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    impersonator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_impersonation_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "identity_login_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    user_agent = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    outcome = table.Column<int>(type: "integer", nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    attempted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_login_attempts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "identity_password_reset_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    issued_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_password_reset_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "identity_refresh_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    issued_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    replaced_by_token_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_refresh_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "identity_roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    scope = table.Column<int>(type: "integer", nullable: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "identity_tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    locale = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_tenants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "identity_two_factor_secrets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    secret = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    recovery_codes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    enabled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_two_factor_secrets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "identity_user_roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    granted_by = table.Column<Guid>(type: "uuid", nullable: false),
                    granted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_user_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "identity_user_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    device_type = table.Column<int>(type: "integer", nullable: false),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    user_agent = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_user_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "identity_users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_type = table.Column<int>(type: "integer", nullable: false),
                    managed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    username = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    normalized_username = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    normalized_email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    email_verified = table.Column<bool>(type: "boolean", nullable: false),
                    email_verified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    phone_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    phone_verified = table.Column<bool>(type: "boolean", nullable: false),
                    full_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    full_name_en = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    password_hash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    password_changed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    requires_password_reset = table.Column<bool>(type: "boolean", nullable: false),
                    two_factor_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    locale = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    failed_login_attempts = table.Column<int>(type: "integer", nullable: false),
                    lockout_end = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_login_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_login_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_users", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_identity_email_verification_tokens_token_hash",
                table: "identity_email_verification_tokens",
                column: "token_hash");

            migrationBuilder.CreateIndex(
                name: "IX_identity_email_verification_tokens_user_id_used_at",
                table: "identity_email_verification_tokens",
                columns: new[] { "user_id", "used_at" });

            migrationBuilder.CreateIndex(
                name: "IX_identity_impersonation_sessions_impersonator_id",
                table: "identity_impersonation_sessions",
                column: "impersonator_id");

            migrationBuilder.CreateIndex(
                name: "IX_identity_impersonation_sessions_target_user_id",
                table: "identity_impersonation_sessions",
                column: "target_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_identity_login_attempts_email_attempted_at",
                table: "identity_login_attempts",
                columns: new[] { "email", "attempted_at" });

            migrationBuilder.CreateIndex(
                name: "IX_identity_login_attempts_ip_address_attempted_at",
                table: "identity_login_attempts",
                columns: new[] { "ip_address", "attempted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_identity_password_reset_tokens_token_hash",
                table: "identity_password_reset_tokens",
                column: "token_hash");

            migrationBuilder.CreateIndex(
                name: "IX_identity_password_reset_tokens_user_id_used_at",
                table: "identity_password_reset_tokens",
                columns: new[] { "user_id", "used_at" });

            migrationBuilder.CreateIndex(
                name: "IX_identity_refresh_tokens_session_id_revoked_at",
                table: "identity_refresh_tokens",
                columns: new[] { "session_id", "revoked_at" });

            migrationBuilder.CreateIndex(
                name: "ix_identity_refresh_tokens_token_hash",
                table: "identity_refresh_tokens",
                column: "token_hash");

            migrationBuilder.CreateIndex(
                name: "IX_identity_refresh_tokens_user_id",
                table: "identity_refresh_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_identity_roles_name_unique",
                table: "identity_roles",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_identity_tenants_status",
                table: "identity_tenants",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_identity_tenants_type_display_name",
                table: "identity_tenants",
                columns: new[] { "type", "display_name" });

            migrationBuilder.CreateIndex(
                name: "ix_identity_two_factor_secrets_user_unique",
                table: "identity_two_factor_secrets",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_identity_user_roles_active_unique",
                table: "identity_user_roles",
                columns: new[] { "user_id", "role_id", "tenant_id" },
                unique: true,
                filter: "revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_identity_user_roles_role_id",
                table: "identity_user_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "IX_identity_user_roles_user_id",
                table: "identity_user_roles",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_identity_user_sessions_user_id_revoked_at",
                table: "identity_user_sessions",
                columns: new[] { "user_id", "revoked_at" });

            migrationBuilder.CreateIndex(
                name: "IX_identity_users_managed_by_user_id",
                table: "identity_users",
                column: "managed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_identity_users_normalized_email_unique",
                table: "identity_users",
                column: "normalized_email",
                unique: true,
                filter: "normalized_email IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_identity_users_normalized_username_unique",
                table: "identity_users",
                column: "normalized_username",
                unique: true,
                filter: "normalized_username IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_identity_users_status",
                table: "identity_users",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_identity_users_tenant_id",
                table: "identity_users",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "identity_email_verification_tokens");

            migrationBuilder.DropTable(
                name: "identity_impersonation_sessions");

            migrationBuilder.DropTable(
                name: "identity_login_attempts");

            migrationBuilder.DropTable(
                name: "identity_password_reset_tokens");

            migrationBuilder.DropTable(
                name: "identity_refresh_tokens");

            migrationBuilder.DropTable(
                name: "identity_roles");

            migrationBuilder.DropTable(
                name: "identity_tenants");

            migrationBuilder.DropTable(
                name: "identity_two_factor_secrets");

            migrationBuilder.DropTable(
                name: "identity_user_roles");

            migrationBuilder.DropTable(
                name: "identity_user_sessions");

            migrationBuilder.DropTable(
                name: "identity_users");

            migrationBuilder.DropColumn(
                name: "user_id",
                table: "teachers");

            migrationBuilder.DropColumn(
                name: "user_id",
                table: "student_profiles");

            migrationBuilder.DropColumn(
                name: "user_id",
                table: "school_administrators");

            migrationBuilder.DropColumn(
                name: "user_id",
                table: "parent_profiles");
        }
    }
}

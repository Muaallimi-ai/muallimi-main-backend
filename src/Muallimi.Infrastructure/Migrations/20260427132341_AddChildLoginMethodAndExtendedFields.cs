using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muallimi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChildLoginMethodAndExtendedFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AvatarBgColor",
                table: "student_profiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrefGoal",
                table: "student_profiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrefLevel",
                table: "student_profiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrefStyles",
                table: "student_profiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SchoolName",
                table: "student_profiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LoginMethod",
                table: "identity_users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PinHash",
                table: "identity_users",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvatarBgColor",
                table: "student_profiles");

            migrationBuilder.DropColumn(
                name: "PrefGoal",
                table: "student_profiles");

            migrationBuilder.DropColumn(
                name: "PrefLevel",
                table: "student_profiles");

            migrationBuilder.DropColumn(
                name: "PrefStyles",
                table: "student_profiles");

            migrationBuilder.DropColumn(
                name: "SchoolName",
                table: "student_profiles");

            migrationBuilder.DropColumn(
                name: "LoginMethod",
                table: "identity_users");

            migrationBuilder.DropColumn(
                name: "PinHash",
                table: "identity_users");
        }
    }
}

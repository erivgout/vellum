using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Data.Migrations
{
    /// <inheritdoc />
    public partial class LoginBlock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LoginBlockAllowList",
                table: "Settings",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LoginBlockMessage",
                table: "Settings",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LoginBlocked",
                table: "Settings",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LoginBlockAllowList",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "LoginBlockMessage",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "LoginBlocked",
                table: "Settings");
        }
    }
}

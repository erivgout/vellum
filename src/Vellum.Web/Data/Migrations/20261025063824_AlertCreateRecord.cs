using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Data.Migrations
{
    /// <inheritdoc />
    public partial class AlertCreateRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreateJson",
                table: "InsightsAlertTemplates",
                type: "nvarchar(max)",
                maxLength: -1,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InsightsAlertCreations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReportId = table.Column<long>(type: "bigint", nullable: false),
                    TemplateId = table.Column<int>(type: "int", nullable: false),
                    ItemKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ItemLabel = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RecordTitle = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    DetailJson = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    SourcesJson = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: false),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InsightsAlertCreations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InsightsAlertCreations_RecordId",
                table: "InsightsAlertCreations",
                column: "RecordId");

            migrationBuilder.CreateIndex(
                name: "IX_InsightsAlertCreations_ReportId",
                table: "InsightsAlertCreations",
                column: "ReportId");

            migrationBuilder.CreateIndex(
                name: "IX_InsightsAlertCreations_TemplateId_ItemKey",
                table: "InsightsAlertCreations",
                columns: new[] { "TemplateId", "ItemKey" });

            migrationBuilder.CreateIndex(
                name: "UX_InsightsAlertCreations_Created",
                table: "InsightsAlertCreations",
                columns: new[] { "ReportId", "ItemKey" },
                unique: true,
                filter: "[Status] = 'Created'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InsightsAlertCreations");

            migrationBuilder.DropColumn(
                name: "CreateJson",
                table: "InsightsAlertTemplates");
        }
    }
}

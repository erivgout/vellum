using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Data.Migrations
{
    /// <inheritdoc />
    public partial class PdfRenditions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PdfConversions",
                columns: table => new
                {
                    SourceSha = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PdfSha = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PdfSize = table.Column<long>(type: "bigint", nullable: false),
                    Converter = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Pages = table.Column<int>(type: "int", nullable: false),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    ConvertedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PdfConversions", x => x.SourceSha);
                });

            migrationBuilder.CreateTable(
                name: "PdfJobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AttachmentId = table.Column<long>(type: "bigint", nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FieldKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RowId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FileName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    SourceSha = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PhaseKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Watermark = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Trigger = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LeaseUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Converter = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Permanent = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DurationMs = table.Column<int>(type: "int", nullable: true),
                    RequestedById = table.Column<int>(type: "int", nullable: true),
                    RenditionId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PdfJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PdfProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FormKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    AutoMark = table.Column<bool>(type: "bit", nullable: false),
                    Color = table.Column<string>(type: "nvarchar(9)", maxLength: 9, nullable: false),
                    Opacity = table.Column<int>(type: "int", nullable: false),
                    Manifest = table.Column<bool>(type: "bit", nullable: false),
                    ManifestNote = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedById = table.Column<int>(type: "int", nullable: true),
                    Origin = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PdfProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PdfFieldSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProfileId = table.Column<int>(type: "int", nullable: false),
                    FieldKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PhaseKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Convert = table.Column<bool>(type: "bit", nullable: false),
                    Require = table.Column<bool>(type: "bit", nullable: false),
                    PdfOnly = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PdfFieldSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PdfFieldSettings_PdfProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "PdfProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PdfPhaseSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProfileId = table.Column<int>(type: "int", nullable: false),
                    PhaseKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Watermark = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Position = table.Column<int>(type: "int", nullable: false),
                    DownloadRole = table.Column<int>(type: "int", nullable: false),
                    PrintRole = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PdfPhaseSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PdfPhaseSettings_PdfProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "PdfProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PdfFieldSettings_ProfileId_FieldKey_PhaseKey",
                table: "PdfFieldSettings",
                columns: new[] { "ProfileId", "FieldKey", "PhaseKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PdfJobs_AttachmentId",
                table: "PdfJobs",
                column: "AttachmentId");

            migrationBuilder.CreateIndex(
                name: "IX_PdfJobs_RecordId",
                table: "PdfJobs",
                column: "RecordId");

            migrationBuilder.CreateIndex(
                name: "IX_PdfJobs_Status_NextAttemptAt",
                table: "PdfJobs",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PdfPhaseSettings_ProfileId_PhaseKey",
                table: "PdfPhaseSettings",
                columns: new[] { "ProfileId", "PhaseKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PdfProfiles_FormKey",
                table: "PdfProfiles",
                column: "FormKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PdfConversions");

            migrationBuilder.DropTable(
                name: "PdfFieldSettings");

            migrationBuilder.DropTable(
                name: "PdfJobs");

            migrationBuilder.DropTable(
                name: "PdfPhaseSettings");

            migrationBuilder.DropTable(
                name: "PdfProfiles");
        }
    }
}

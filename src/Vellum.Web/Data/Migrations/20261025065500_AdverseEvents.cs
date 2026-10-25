using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Data.Migrations
{
    /// <inheritdoc />
    public partial class AdverseEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AerEmdrProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EnvironmentKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    EnvironmentName = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    AccountType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SenderOrganization = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    SenderRoutingId = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    SenderEmail = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AcknowledgementUrl = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    FdaRoutingId = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    EsgUrl = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    SubmissionCenter = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Gateway = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SimulatedOutcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FdaCertificatePem = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    CertCommonName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CertOrganization = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CertOrganizationalUnit = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CertLocality = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CertState = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CertCountry = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    CertEmail = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CertPem = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    CertThumbprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CertNotBefore = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CertNotAfter = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CertGeneratedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CertFdaConfirmedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PrivateKeyProtected = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    EsgPasswordProtected = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    EsgPasswordSetAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedById = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AerEmdrProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AerEmdrSubmissions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MedWatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Attempt = table.Column<int>(type: "int", nullable: false),
                    Gateway = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    MessageId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ReportNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FileSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AttachmentId = table.Column<long>(type: "bigint", nullable: true),
                    FormVersion = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    SchemaVersion = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    RuleSetVersion = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    AccountType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<int>(type: "int", nullable: false),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Ack1At = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Ack2At = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Ack3At = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    SimulatedOutcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Superseded = table.Column<bool>(type: "bit", nullable: false),
                    OverdueAlertedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailureAlertedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeadlineAlertedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AerEmdrSubmissions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AerMedWatchReports",
                columns: table => new
                {
                    MedWatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ComplaintId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MdrTreeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsInitial = table.Column<bool>(type: "bit", nullable: false),
                    ReporterType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsVoided = table.Column<bool>(type: "bit", nullable: false),
                    OriginalMedWatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FollowUpNumber = table.Column<int>(type: "int", nullable: true),
                    ReportCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ReportYear = table.Column<int>(type: "int", nullable: true),
                    ReportSequence = table.Column<int>(type: "int", nullable: true),
                    SequenceGenerated = table.Column<bool>(type: "bit", nullable: false),
                    NumberAssignedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AerMedWatchReports", x => x.MedWatchId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AerEmdrProfiles_EnvironmentKey",
                table: "AerEmdrProfiles",
                column: "EnvironmentKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AerEmdrSubmissions_MedWatchId_Attempt",
                table: "AerEmdrSubmissions",
                columns: new[] { "MedWatchId", "Attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AerEmdrSubmissions_MessageId",
                table: "AerEmdrSubmissions",
                column: "MessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AerEmdrSubmissions_Status",
                table: "AerEmdrSubmissions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AerMedWatchReports_ComplaintId",
                table: "AerMedWatchReports",
                column: "ComplaintId");

            migrationBuilder.CreateIndex(
                name: "UX_AerMedWatch_FollowUp",
                table: "AerMedWatchReports",
                columns: new[] { "OriginalMedWatchId", "FollowUpNumber" },
                unique: true,
                filter: "[OriginalMedWatchId] IS NOT NULL AND [FollowUpNumber] IS NOT NULL AND [IsVoided] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_AerMedWatch_InitialPerTree",
                table: "AerMedWatchReports",
                column: "MdrTreeId",
                unique: true,
                filter: "[MdrTreeId] IS NOT NULL AND [IsInitial] = 1 AND [IsVoided] = 0 AND [ReporterType] = 'manufacturer'");

            migrationBuilder.CreateIndex(
                name: "UX_AerMedWatch_Number",
                table: "AerMedWatchReports",
                columns: new[] { "ReporterType", "ReportCode", "ReportYear", "ReportSequence" },
                unique: true,
                filter: "[IsInitial] = 1 AND [ReportSequence] IS NOT NULL AND [ReportCode] IS NOT NULL AND [ReportYear] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AerEmdrProfiles");

            migrationBuilder.DropTable(
                name: "AerEmdrSubmissions");

            migrationBuilder.DropTable(
                name: "AerMedWatchReports");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Data.Migrations
{
    /// <inheritdoc />
    public partial class SharePointPublishing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SharePointAttempts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    JobId = table.Column<long>(type: "bigint", nullable: false),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    ErrorCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharePointAttempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SharePointJobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuleId = table.Column<int>(type: "int", nullable: true),
                    Trigger = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    MaxAttempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ErrorCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Result = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharePointJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SharePointMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FormKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharePointMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SharePointProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Host = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: false),
                    Connection = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ClientId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AuthMethod = table.Column<int>(type: "int", nullable: false),
                    SecretProtected = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    SecretSetAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CertificateProtected = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    CertificatePasswordProtected = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    CertificateThumbprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CertificateSubject = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CertificateExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NotifyUserIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    SimulateOutage = table.Column<bool>(type: "bit", nullable: false),
                    LastTestAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastTestOk = table.Column<bool>(type: "bit", nullable: true),
                    LastTestSummary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharePointProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SharePointPublications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Revision = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    RevisionOrdinal = table.Column<int>(type: "int", nullable: false),
                    RuleId = table.Column<int>(type: "int", nullable: false),
                    ProfileId = table.Column<int>(type: "int", nullable: false),
                    LibraryId = table.Column<int>(type: "int", nullable: false),
                    FieldKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AttachmentId = table.Column<long>(type: "bigint", nullable: false),
                    OriginalAttachmentId = table.Column<long>(type: "bigint", nullable: true),
                    IsRendition = table.Column<bool>(type: "bit", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    FolderPath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    PathKey = table.Column<string>(type: "nvarchar(700)", maxLength: 700, nullable: false),
                    WebUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RemoteId = table.Column<string>(type: "nvarchar(700)", maxLength: 700, nullable: true),
                    MetadataJson = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    WithdrawnAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    WithdrawnReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ReplacedById = table.Column<long>(type: "bigint", nullable: true),
                    JobId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharePointPublications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SharePointRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    FormKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PhaseKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OnEnter = table.Column<bool>(type: "bit", nullable: false),
                    OnSave = table.Column<bool>(type: "bit", nullable: false),
                    ProfileId = table.Column<int>(type: "int", nullable: false),
                    LibraryId = table.Column<int>(type: "int", nullable: false),
                    FieldKeys = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FolderTemplate = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    FileNameTemplate = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    PreferRendition = table.Column<bool>(type: "bit", nullable: false),
                    FallbackToOriginal = table.Column<bool>(type: "bit", nullable: false),
                    MappingId = table.Column<int>(type: "int", nullable: true),
                    EffectiveOnly = table.Column<bool>(type: "bit", nullable: false),
                    KeepVersionHistory = table.Column<bool>(type: "bit", nullable: false),
                    WithdrawAction = table.Column<int>(type: "int", nullable: false),
                    ObsoleteFolder = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharePointRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SharePointMappingEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MappingId = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Column = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharePointMappingEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SharePointMappingEntries_SharePointMappings_MappingId",
                        column: x => x.MappingId,
                        principalTable: "SharePointMappings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SharePointLibraries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProfileId = table.Column<int>(type: "int", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    RelativeUrl = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    SimulatedColumns = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharePointLibraries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SharePointLibraries_SharePointProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "SharePointProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SharePointAttempts_JobId",
                table: "SharePointAttempts",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_SharePointJobs_RecordId",
                table: "SharePointJobs",
                column: "RecordId");

            migrationBuilder.CreateIndex(
                name: "IX_SharePointJobs_Status_NextAttemptAt",
                table: "SharePointJobs",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SharePointLibraries_ProfileId_Key",
                table: "SharePointLibraries",
                columns: new[] { "ProfileId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SharePointMappingEntries_MappingId",
                table: "SharePointMappingEntries",
                column: "MappingId");

            migrationBuilder.CreateIndex(
                name: "IX_SharePointProfiles_Host",
                table: "SharePointProfiles",
                column: "Host",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SharePointPublications_FamilyId",
                table: "SharePointPublications",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_SharePointPublications_LibraryId_Status",
                table: "SharePointPublications",
                columns: new[] { "LibraryId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SharePointPublications_RecordId",
                table: "SharePointPublications",
                column: "RecordId");

            migrationBuilder.CreateIndex(
                name: "IX_SharePointRules_FormKey",
                table: "SharePointRules",
                column: "FormKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SharePointAttempts");

            migrationBuilder.DropTable(
                name: "SharePointJobs");

            migrationBuilder.DropTable(
                name: "SharePointLibraries");

            migrationBuilder.DropTable(
                name: "SharePointMappingEntries");

            migrationBuilder.DropTable(
                name: "SharePointPublications");

            migrationBuilder.DropTable(
                name: "SharePointRules");

            migrationBuilder.DropTable(
                name: "SharePointProfiles");

            migrationBuilder.DropTable(
                name: "SharePointMappings");
        }
    }
}

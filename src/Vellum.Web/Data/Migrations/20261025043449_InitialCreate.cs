using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Apps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Key = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Icon = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Color = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Suite = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    DefaultAccess = table.Column<int>(type: "int", nullable: false),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false),
                    Disabled = table.Column<bool>(type: "bit", nullable: false),
                    HideMyAssignments = table.Column<bool>(type: "bit", nullable: false),
                    PackageVersion = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ProtectFromUpgrade = table.Column<bool>(type: "bit", nullable: false),
                    DeleterUsernames = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Apps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AssetTagClaims",
                columns: table => new
                {
                    Tag = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetTagClaims", x => x.Tag);
                });

            migrationBuilder.CreateTable(
                name: "AuditEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    UserName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    OnBehalfOfId = table.Column<int>(type: "int", nullable: true),
                    OnBehalfOfName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ActorKind = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    FieldKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    FieldLabel = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    OldValue = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Ip = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    PhaseName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RecordVersion = table.Column<int>(type: "int", nullable: true),
                    PrevHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CalendarDays",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    IsWorking = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarDays", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConfigAuditEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    UserName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ObjectType = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ObjectKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Before = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    After = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    ConfigVersion = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConfigVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Number = table.Column<int>(type: "int", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PublishedById = table.Column<int>(type: "int", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeletionHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    AppName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    FormName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Number = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Revision = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Snapshot = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeletionHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DesignerDrafts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AppKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IsNewApp = table.Column<bool>(type: "bit", nullable: false),
                    Json = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: false),
                    BaseHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BaseVersion = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<int>(type: "int", nullable: false),
                    CreatedByName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedById = table.Column<int>(type: "int", nullable: false),
                    UpdatedByName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DesignerDrafts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DesignerLocks",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AppKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Component = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    AcquiredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastActivityAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DesignerLocks", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "DesignerPublications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    AppKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AppName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PublishedById = table.Column<int>(type: "int", nullable: true),
                    PublishedByName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Changes = table.Column<int>(type: "int", nullable: false),
                    AcceptedWarnings = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    Impact = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    Snapshot = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: false),
                    RestoredFromVersion = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DesignerPublications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmailLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    To = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Favorites",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    RefKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Favorites", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FileBlobs",
                columns: table => new
                {
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    StoredAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileBlobs", x => x.Sha256);
                });

            migrationBuilder.CreateTable(
                name: "Groups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Key = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Inactive = table.Column<bool>(type: "bit", nullable: false),
                    IsTrainingGroup = table.Column<bool>(type: "bit", nullable: false),
                    LocationId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Groups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InsightsAlertReports",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Number = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    TemplateId = table.Column<int>(type: "int", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsTest = table.Column<bool>(type: "bit", nullable: false),
                    Manual = table.Column<bool>(type: "bit", nullable: false),
                    OriginJson = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: false),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: false),
                    GroupCount = table.Column<int>(type: "int", nullable: false),
                    RecordCount = table.Column<int>(type: "int", nullable: false),
                    ActionStatus = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ActionDetail = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ResultHash = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    NotifiedJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InsightsAlertReports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InsightsAlertTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Number = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    OwnerId = table.Column<int>(type: "int", nullable: false),
                    AppKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ViewKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    MatchColumnsJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ReportColumnsJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    MinMatches = table.Column<int>(type: "int", nullable: false),
                    LookbackDays = table.Column<int>(type: "int", nullable: true),
                    WindowStart = table.Column<DateOnly>(type: "date", nullable: true),
                    WindowEnd = table.Column<DateOnly>(type: "date", nullable: true),
                    IncrementDates = table.Column<bool>(type: "bit", nullable: false),
                    FrequencyDays = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    OnlyWhenChanged = table.Column<bool>(type: "bit", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    RecipientsJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CcJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    LastRunAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastReportAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InsightsAlertTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InsightsCharts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Number = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    IsTemplate = table.Column<bool>(type: "bit", nullable: false),
                    TemplateId = table.Column<int>(type: "int", nullable: true),
                    OwnerId = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    AppKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ViewKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: false),
                    LocationId = table.Column<int>(type: "int", nullable: true),
                    Disabled = table.Column<bool>(type: "bit", nullable: false),
                    Pinned = table.Column<bool>(type: "bit", nullable: false),
                    PinMode = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InsightsCharts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KeywordLists",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Key = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeywordLists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Locations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ParentId = table.Column<int>(type: "int", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Path = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    TimeZone = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Disabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Locations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Locations_Locations_ParentId",
                        column: x => x.ParentId,
                        principalTable: "Locations",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Url = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcknowledgedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NumberSequences",
                columns: table => new
                {
                    Scope = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Next = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NumberSequences", x => x.Scope);
                });

            migrationBuilder.CreateTable(
                name: "Outbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Type = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Outbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PasswordHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Hash = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasswordHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecordAccess",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrincipalKind = table.Column<int>(type: "int", nullable: false),
                    PrincipalId = table.Column<int>(type: "int", nullable: false),
                    Level = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordAccess", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SecurityEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    Username = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Ip = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    EnvironmentName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    EnvironmentType = table.Column<int>(type: "int", nullable: false),
                    BannerText = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RegulatedMode = table.Column<bool>(type: "bit", nullable: false),
                    OrganizationName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    SessionIdleMinutes = table.Column<int>(type: "int", nullable: false),
                    SessionAbsoluteHours = table.Column<int>(type: "int", nullable: false),
                    PasswordMinLength = table.Column<int>(type: "int", nullable: false),
                    PasswordHistoryCount = table.Column<int>(type: "int", nullable: false),
                    PasswordExpiryDays = table.Column<int>(type: "int", nullable: false),
                    LockoutThreshold = table.Column<int>(type: "int", nullable: false),
                    MfaRequired = table.Column<bool>(type: "bit", nullable: false),
                    LockTimeoutMinutes = table.Column<int>(type: "int", nullable: false),
                    EscalateToManagerAfterDays = table.Column<int>(type: "int", nullable: false),
                    SystemIdentityName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    GlobalSearchEnabled = table.Column<bool>(type: "bit", nullable: false),
                    EmptyValueSymbol = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    LabelForward = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    LabelBack = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    LabelVoid = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ESignatureCertification = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    LoginMessage = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Signatures",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SignerId = table.Column<int>(type: "int", nullable: false),
                    SignerName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    SignerUsername = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    OnBehalfOfId = table.Column<int>(type: "int", nullable: true),
                    OnBehalfOfName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Meaning = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    PhaseKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    PhaseName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    PhaseOrder = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    SignedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RecordHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RecordVersion = table.Column<int>(type: "int", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Superseded = table.Column<bool>(type: "bit", nullable: false),
                    SupersededAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Signatures", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SubformRows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SectionKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubformRows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaskRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TaskId = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    RecordsAffected = table.Column<int>(type: "int", nullable: false),
                    Log = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    Manual = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Key = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    IntervalMinutes = table.Column<int>(type: "int", nullable: false),
                    LastRunAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NextRunAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastOutcome = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    LeaseUntil = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Forms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AppId = table.Column<int>(type: "int", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Icon = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Disabled = table.Column<bool>(type: "bit", nullable: false),
                    IsActivity = table.Column<bool>(type: "bit", nullable: false),
                    ParentForms = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsNonWorkflow = table.Column<bool>(type: "bit", nullable: false),
                    HiddenFromNew = table.Column<bool>(type: "bit", nullable: false),
                    NumberPrefix = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    NumberSuffix = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    NumberPadding = table.Column<int>(type: "int", nullable: false),
                    NumberIncludesYear = table.Column<bool>(type: "bit", nullable: false),
                    NumberScope = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    TitleTemplate = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    RevisionControlled = table.Column<bool>(type: "bit", nullable: false),
                    RevisionFormat = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Forms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Forms_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Grants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AppId = table.Column<int>(type: "int", nullable: false),
                    FormKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    PrincipalKind = table.Column<int>(type: "int", nullable: false),
                    PrincipalId = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    IsDesigner = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Grants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Grants_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Views",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AppId = table.Column<int>(type: "int", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Forms = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Columns = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Phases = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AssignedToMe = table.Column<bool>(type: "bit", nullable: false),
                    CreatedByMe = table.Column<bool>(type: "bit", nullable: false),
                    OverdueOnly = table.Column<bool>(type: "bit", nullable: false),
                    FilterJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    SortBy = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    SortDesc = table.Column<bool>(type: "bit", nullable: false),
                    PageSize = table.Column<int>(type: "int", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    Hidden = table.Column<bool>(type: "bit", nullable: false),
                    CalendarField = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Views", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Views_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Keywords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ListId = table.Column<int>(type: "int", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Color = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Disabled = table.Column<bool>(type: "bit", nullable: false),
                    ParentId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Keywords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Keywords_KeywordLists_ListId",
                        column: x => x.ListId,
                        principalTable: "KeywordLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Username = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    JobTitle = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Department = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    PasswordHash = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    PasswordChangedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MustChangePassword = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StatusReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    FailedLoginCount = table.Column<int>(type: "int", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PrimaryLocationId = table.Column<int>(type: "int", nullable: true),
                    TimeZone = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Locale = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ReportsToId = table.Column<int>(type: "int", nullable: true),
                    IsAdministrator = table.Column<bool>(type: "bit", nullable: false),
                    IsSystemIdentity = table.Column<bool>(type: "bit", nullable: false),
                    License = table.Column<int>(type: "int", nullable: false),
                    IsExternal = table.Column<bool>(type: "bit", nullable: false),
                    ESignatureCertifiedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TotpSecret = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    MfaEnrolled = table.Column<bool>(type: "bit", nullable: false),
                    DefaultAppKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Theme = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Users_Locations_PrimaryLocationId",
                        column: x => x.PrimaryLocationId,
                        principalTable: "Locations",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Users_Users_ReportsToId",
                        column: x => x.ReportsToId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Fields",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FormId = table.Column<int>(type: "int", nullable: false),
                    SectionKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Key = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Width = table.Column<int>(type: "int", nullable: false),
                    Required = table.Column<bool>(type: "bit", nullable: false),
                    RequiredInPhases = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PreventNull = table.Column<bool>(type: "bit", nullable: false),
                    Unique = table.Column<bool>(type: "bit", nullable: false),
                    Sensitive = table.Column<bool>(type: "bit", nullable: false),
                    ReadOnly = table.Column<bool>(type: "bit", nullable: false),
                    ProtectFromAdminUpdate = table.Column<bool>(type: "bit", nullable: false),
                    KeywordList = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    LinkForms = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Help = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Placeholder = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    DefaultValue = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Min = table.Column<decimal>(type: "decimal(38,10)", precision: 38, scale: 10, nullable: true),
                    Max = table.Column<decimal>(type: "decimal(38,10)", precision: 38, scale: 10, nullable: true),
                    Decimals = table.Column<int>(type: "int", nullable: true),
                    MaxLength = table.Column<int>(type: "int", nullable: true),
                    CurrencyCode = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ShowInHeader = table.Column<bool>(type: "bit", nullable: false),
                    VisibleWhen = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Fields", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Fields_Forms_FormId",
                        column: x => x.FormId,
                        principalTable: "Forms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Sections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FormId = table.Column<int>(type: "int", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Tab = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Icon = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    IsSubform = table.Column<bool>(type: "bit", nullable: false),
                    RowNoun = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    MaxRows = table.Column<int>(type: "int", nullable: true),
                    Required = table.Column<bool>(type: "bit", nullable: false),
                    Optional = table.Column<bool>(type: "bit", nullable: false),
                    Collapsed = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sections_Forms_FormId",
                        column: x => x.FormId,
                        principalTable: "Forms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Workflows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FormId = table.Column<int>(type: "int", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Disabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workflows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Workflows_Forms_FormId",
                        column: x => x.FormId,
                        principalTable: "Forms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Attachments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FieldKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    UploadedById = table.Column<int>(type: "int", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Removed = table.Column<bool>(type: "bit", nullable: false),
                    RemovedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RemovedById = table.Column<int>(type: "int", nullable: true),
                    RenditionOfId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Attachments_Users_UploadedById",
                        column: x => x.UploadedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Comments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    Text = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: false),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Comments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Comments_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Delegations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequesterId = table.Column<int>(type: "int", nullable: false),
                    DelegateId = table.Column<int>(type: "int", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Delegations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Delegations_Users_DelegateId",
                        column: x => x.DelegateId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Delegations_Users_RequesterId",
                        column: x => x.RequesterId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupMembers",
                columns: table => new
                {
                    GroupId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMembers", x => new { x.GroupId, x.UserId });
                    table.ForeignKey(
                        name: "FK_GroupMembers_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GroupMembers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Locks",
                columns: table => new
                {
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    AcquiredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastActivityAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Locks", x => x.RecordId);
                    table.ForeignKey(
                        name: "FK_Locks_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AbsoluteExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Ip = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sessions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Phases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkflowId = table.Column<int>(type: "int", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Type = table.Column<int>(type: "int", nullable: false),
                    IsStart = table.Column<bool>(type: "bit", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    EditableSections = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReadOnlySections = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    HiddenSections = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Assignment = table.Column<int>(type: "int", nullable: false),
                    AssigneeField = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    AssigneeGroup = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    AssignAllMembers = table.Column<bool>(type: "bit", nullable: false),
                    Routing = table.Column<int>(type: "int", nullable: false),
                    QuorumPercent = table.Column<int>(type: "int", nullable: false),
                    DueDays = table.Column<int>(type: "int", nullable: true),
                    DueDateField = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    BusinessDays = table.Column<bool>(type: "bit", nullable: false),
                    AssignerCanChangeDue = table.Column<bool>(type: "bit", nullable: false),
                    MustCreateForms = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MustCompleteForms = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ActivityForms = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Color = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Phases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Phases_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "Workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PhaseEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PhaseId = table.Column<int>(type: "int", nullable: false),
                    PhaseName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    EnteredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EnteredById = table.Column<int>(type: "int", nullable: true),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ExitedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExitedById = table.Column<int>(type: "int", nullable: true),
                    ExitAction = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ConfigVersion = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhaseEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhaseEntries_Phases_PhaseId",
                        column: x => x.PhaseId,
                        principalTable: "Phases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppId = table.Column<int>(type: "int", nullable: false),
                    FormId = table.Column<int>(type: "int", nullable: false),
                    WorkflowId = table.Column<int>(type: "int", nullable: true),
                    PhaseId = table.Column<int>(type: "int", nullable: true),
                    PhaseType = table.Column<int>(type: "int", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    LocationId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<int>(type: "int", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedById = table.Column<int>(type: "int", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Revision = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RevisionOrdinal = table.Column<int>(type: "int", nullable: false),
                    IsEffective = table.Column<bool>(type: "bit", nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: true),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    ArchiveReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ArchivedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConfigVersion = table.Column<int>(type: "int", nullable: false),
                    LegalHold = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Records", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Records_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Records_Forms_FormId",
                        column: x => x.FormId,
                        principalTable: "Forms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Records_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Records_Phases_PhaseId",
                        column: x => x.PhaseId,
                        principalTable: "Phases",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Records_Records_ParentId",
                        column: x => x.ParentId,
                        principalTable: "Records",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Records_Users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Records_Users_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Records_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "Workflows",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Routes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkflowId = table.Column<int>(type: "int", nullable: false),
                    FromPhaseId = table.Column<int>(type: "int", nullable: false),
                    ToPhaseId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Order = table.Column<int>(type: "int", nullable: false),
                    RequireComment = table.Column<bool>(type: "bit", nullable: false),
                    SignatureMeaning = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Condition = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    AllowedPrincipals = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Routes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Routes_Phases_FromPhaseId",
                        column: x => x.FromPhaseId,
                        principalTable: "Phases",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Routes_Phases_ToPhaseId",
                        column: x => x.ToPhaseId,
                        principalTable: "Phases",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Routes_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "Workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Assignments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PhaseEntryId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AssignedById = table.Column<int>(type: "int", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Vote = table.Column<int>(type: "int", nullable: false),
                    VoteComment = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    LastReminderAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Assignments_PhaseEntries_PhaseEntryId",
                        column: x => x.PhaseEntryId,
                        principalTable: "PhaseEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Assignments_Records_RecordId",
                        column: x => x.RecordId,
                        principalTable: "Records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Assignments_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Links",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FromId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ToId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FieldKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ById = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Links_Records_FromId",
                        column: x => x.FromId,
                        principalTable: "Records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Links_Records_ToId",
                        column: x => x.ToId,
                        principalTable: "Records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecordValues",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FieldKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Index = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    Number = table.Column<decimal>(type: "decimal(38,10)", precision: 38, scale: 10, nullable: true),
                    Date = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RefId = table.Column<int>(type: "int", nullable: true),
                    RefGuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecordValues_Records_RecordId",
                        column: x => x.RecordId,
                        principalTable: "Records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Apps_Key",
                table: "Apps",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetTagClaims_RecordId",
                table: "AssetTagClaims",
                column: "RecordId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_PhaseEntryId",
                table: "Assignments",
                column: "PhaseEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_RecordId",
                table: "Assignments",
                column: "RecordId");

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_UserId_Status",
                table: "Assignments",
                columns: new[] { "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_RecordId_FieldKey",
                table: "Attachments",
                columns: new[] { "RecordId", "FieldKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_UploadedById",
                table: "Attachments",
                column: "UploadedById");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_At",
                table: "AuditEntries",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_RecordId_Seq",
                table: "AuditEntries",
                columns: new[] { "RecordId", "Seq" },
                unique: true,
                filter: "[RecordId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Audit_GlobalSeq",
                table: "AuditEntries",
                column: "Seq",
                unique: true,
                filter: "[RecordId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarDays_Date",
                table: "CalendarDays",
                column: "Date",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Comments_RecordId",
                table: "Comments",
                column: "RecordId");

            migrationBuilder.CreateIndex(
                name: "IX_Comments_UserId",
                table: "Comments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigAuditEntries_At",
                table: "ConfigAuditEntries",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigVersions_Number",
                table: "ConfigVersions",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Delegations_DelegateId",
                table: "Delegations",
                column: "DelegateId");

            migrationBuilder.CreateIndex(
                name: "IX_Delegations_RequesterId",
                table: "Delegations",
                column: "RequesterId");

            migrationBuilder.CreateIndex(
                name: "IX_DesignerDrafts_AppKey",
                table: "DesignerDrafts",
                column: "AppKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DesignerLocks_AppKey",
                table: "DesignerLocks",
                column: "AppKey");

            migrationBuilder.CreateIndex(
                name: "IX_DesignerPublications_AppKey",
                table: "DesignerPublications",
                column: "AppKey");

            migrationBuilder.CreateIndex(
                name: "IX_DesignerPublications_VersionNumber",
                table: "DesignerPublications",
                column: "VersionNumber");

            migrationBuilder.CreateIndex(
                name: "IX_EmailLog_RecordId",
                table: "EmailLog",
                column: "RecordId");

            migrationBuilder.CreateIndex(
                name: "IX_Favorites_UserId_Kind_RefKey",
                table: "Favorites",
                columns: new[] { "UserId", "Kind", "RefKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Fields_FormId_Key",
                table: "Fields",
                columns: new[] { "FormId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Forms_AppId",
                table: "Forms",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_Forms_Key",
                table: "Forms",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Grants_AppId_PrincipalKind_PrincipalId",
                table: "Grants",
                columns: new[] { "AppId", "PrincipalKind", "PrincipalId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupMembers_UserId",
                table: "GroupMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Groups_Key",
                table: "Groups",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InsightsAlertReports_Number",
                table: "InsightsAlertReports",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InsightsAlertReports_TemplateId_GeneratedAt",
                table: "InsightsAlertReports",
                columns: new[] { "TemplateId", "GeneratedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InsightsAlertTemplates_Number",
                table: "InsightsAlertTemplates",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InsightsCharts_Number",
                table: "InsightsCharts",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InsightsCharts_OwnerId_IsTemplate",
                table: "InsightsCharts",
                columns: new[] { "OwnerId", "IsTemplate" });

            migrationBuilder.CreateIndex(
                name: "IX_KeywordLists_Key",
                table: "KeywordLists",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Keywords_ListId_Key",
                table: "Keywords",
                columns: new[] { "ListId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Links_FromId",
                table: "Links",
                column: "FromId");

            migrationBuilder.CreateIndex(
                name: "IX_Links_ToId",
                table: "Links",
                column: "ToId");

            migrationBuilder.CreateIndex(
                name: "IX_Locations_Code",
                table: "Locations",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Locations_ParentId",
                table: "Locations",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Locks_UserId",
                table: "Locks",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_ReadAt",
                table: "Notifications",
                columns: new[] { "UserId", "ReadAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_ProcessedAt",
                table: "Outbox",
                column: "ProcessedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PasswordHistory_UserId",
                table: "PasswordHistory",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PhaseEntries_PhaseId",
                table: "PhaseEntries",
                column: "PhaseId");

            migrationBuilder.CreateIndex(
                name: "IX_PhaseEntries_RecordId",
                table: "PhaseEntries",
                column: "RecordId");

            migrationBuilder.CreateIndex(
                name: "IX_Phases_WorkflowId_Key",
                table: "Phases",
                columns: new[] { "WorkflowId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecordAccess_RecordId",
                table: "RecordAccess",
                column: "RecordId");

            migrationBuilder.CreateIndex(
                name: "IX_Records_AppId_Number_RevisionOrdinal",
                table: "Records",
                columns: new[] { "AppId", "Number", "RevisionOrdinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Records_CreatedById",
                table: "Records",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Records_DueDate",
                table: "Records",
                column: "DueDate");

            migrationBuilder.CreateIndex(
                name: "IX_Records_FormId_PhaseType",
                table: "Records",
                columns: new[] { "FormId", "PhaseType" });

            migrationBuilder.CreateIndex(
                name: "IX_Records_LocationId",
                table: "Records",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_Records_ModifiedById",
                table: "Records",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_Records_ParentId",
                table: "Records",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Records_PhaseId",
                table: "Records",
                column: "PhaseId");

            migrationBuilder.CreateIndex(
                name: "IX_Records_WorkflowId",
                table: "Records",
                column: "WorkflowId");

            migrationBuilder.CreateIndex(
                name: "UX_Records_Family_Effective",
                table: "Records",
                column: "FamilyId",
                unique: true,
                filter: "[IsEffective] = 1 AND [FamilyId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RecordValues_FieldKey_RefId",
                table: "RecordValues",
                columns: new[] { "FieldKey", "RefId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecordValues_RecordId_FieldKey",
                table: "RecordValues",
                columns: new[] { "RecordId", "FieldKey" });

            migrationBuilder.CreateIndex(
                name: "IX_RecordValues_RefGuid",
                table: "RecordValues",
                column: "RefGuid");

            migrationBuilder.CreateIndex(
                name: "IX_Routes_FromPhaseId",
                table: "Routes",
                column: "FromPhaseId");

            migrationBuilder.CreateIndex(
                name: "IX_Routes_ToPhaseId",
                table: "Routes",
                column: "ToPhaseId");

            migrationBuilder.CreateIndex(
                name: "IX_Routes_WorkflowId",
                table: "Routes",
                column: "WorkflowId");

            migrationBuilder.CreateIndex(
                name: "IX_Sections_FormId_Key",
                table: "Sections",
                columns: new[] { "FormId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_At",
                table: "SecurityEvents",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_UserId",
                table: "Sessions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Signatures_RecordId",
                table: "Signatures",
                column: "RecordId");

            migrationBuilder.CreateIndex(
                name: "IX_SubformRows_RecordId",
                table: "SubformRows",
                column: "RecordId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRuns_TaskId",
                table: "TaskRuns",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_Key",
                table: "Tasks",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_PrimaryLocationId",
                table: "Users",
                column: "PrimaryLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_ReportsToId",
                table: "Users",
                column: "ReportsToId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Views_AppId_Key",
                table: "Views",
                columns: new[] { "AppId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Workflows_FormId_Key",
                table: "Workflows",
                columns: new[] { "FormId", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssetTagClaims");

            migrationBuilder.DropTable(
                name: "Assignments");

            migrationBuilder.DropTable(
                name: "Attachments");

            migrationBuilder.DropTable(
                name: "AuditEntries");

            migrationBuilder.DropTable(
                name: "CalendarDays");

            migrationBuilder.DropTable(
                name: "Comments");

            migrationBuilder.DropTable(
                name: "ConfigAuditEntries");

            migrationBuilder.DropTable(
                name: "ConfigVersions");

            migrationBuilder.DropTable(
                name: "Delegations");

            migrationBuilder.DropTable(
                name: "DeletionHistory");

            migrationBuilder.DropTable(
                name: "DesignerDrafts");

            migrationBuilder.DropTable(
                name: "DesignerLocks");

            migrationBuilder.DropTable(
                name: "DesignerPublications");

            migrationBuilder.DropTable(
                name: "EmailLog");

            migrationBuilder.DropTable(
                name: "Favorites");

            migrationBuilder.DropTable(
                name: "Fields");

            migrationBuilder.DropTable(
                name: "FileBlobs");

            migrationBuilder.DropTable(
                name: "Grants");

            migrationBuilder.DropTable(
                name: "GroupMembers");

            migrationBuilder.DropTable(
                name: "InsightsAlertReports");

            migrationBuilder.DropTable(
                name: "InsightsAlertTemplates");

            migrationBuilder.DropTable(
                name: "InsightsCharts");

            migrationBuilder.DropTable(
                name: "Keywords");

            migrationBuilder.DropTable(
                name: "Links");

            migrationBuilder.DropTable(
                name: "Locks");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "NumberSequences");

            migrationBuilder.DropTable(
                name: "Outbox");

            migrationBuilder.DropTable(
                name: "PasswordHistory");

            migrationBuilder.DropTable(
                name: "RecordAccess");

            migrationBuilder.DropTable(
                name: "RecordValues");

            migrationBuilder.DropTable(
                name: "Routes");

            migrationBuilder.DropTable(
                name: "Sections");

            migrationBuilder.DropTable(
                name: "SecurityEvents");

            migrationBuilder.DropTable(
                name: "Sessions");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "Signatures");

            migrationBuilder.DropTable(
                name: "SubformRows");

            migrationBuilder.DropTable(
                name: "TaskRuns");

            migrationBuilder.DropTable(
                name: "Tasks");

            migrationBuilder.DropTable(
                name: "Views");

            migrationBuilder.DropTable(
                name: "PhaseEntries");

            migrationBuilder.DropTable(
                name: "Groups");

            migrationBuilder.DropTable(
                name: "KeywordLists");

            migrationBuilder.DropTable(
                name: "Records");

            migrationBuilder.DropTable(
                name: "Phases");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Workflows");

            migrationBuilder.DropTable(
                name: "Locations");

            migrationBuilder.DropTable(
                name: "Forms");

            migrationBuilder.DropTable(
                name: "Apps");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gaska.Intranet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "intranet");

            migrationBuilder.CreateTable(
                name: "AuditEntries",
                schema: "intranet",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TimestampUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AccountName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Details = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                schema: "intranet",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FriendlyName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Xml = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoginChallenges",
                schema: "intranet",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CodeHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FailedAttempts = table.Column<int>(type: "int", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoginChallenges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ModuleAccessRules",
                schema: "intranet",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ModuleKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PrincipalKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PrincipalSid = table.Column<string>(type: "nvarchar(184)", maxLength: 184, nullable: false),
                    PrincipalName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModuleAccessRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ModuleSettings",
                schema: "intranet",
                columns: table => new
                {
                    ModuleKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AccessMode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModuleSettings", x => x.ModuleKey);
                });

            migrationBuilder.CreateTable(
                name: "TrustedDevices",
                schema: "intranet",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UserAgent = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    VerifiedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastUsedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrustedDevices", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_AccountName",
                schema: "intranet",
                table: "AuditEntries",
                column: "AccountName");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_TimestampUtc",
                schema: "intranet",
                table: "AuditEntries",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_LoginChallenges_AccountName_CreatedAtUtc",
                schema: "intranet",
                table: "LoginChallenges",
                columns: new[] { "AccountName", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ModuleAccessRules_ModuleKey_PrincipalSid",
                schema: "intranet",
                table: "ModuleAccessRules",
                columns: new[] { "ModuleKey", "PrincipalSid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TrustedDevices_AccountName",
                schema: "intranet",
                table: "TrustedDevices",
                column: "AccountName");

            migrationBuilder.CreateIndex(
                name: "IX_TrustedDevices_ExpiresAtUtc",
                schema: "intranet",
                table: "TrustedDevices",
                column: "ExpiresAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEntries",
                schema: "intranet");

            migrationBuilder.DropTable(
                name: "DataProtectionKeys",
                schema: "intranet");

            migrationBuilder.DropTable(
                name: "LoginChallenges",
                schema: "intranet");

            migrationBuilder.DropTable(
                name: "ModuleAccessRules",
                schema: "intranet");

            migrationBuilder.DropTable(
                name: "ModuleSettings",
                schema: "intranet");

            migrationBuilder.DropTable(
                name: "TrustedDevices",
                schema: "intranet");
        }
    }
}

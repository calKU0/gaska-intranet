using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gaska.Intranet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserAuthenticators : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserAuthenticators",
                schema: "intranet",
                columns: table => new
                {
                    UserSid = table.Column<string>(type: "nvarchar(184)", maxLength: 184, nullable: false),
                    AccountName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ProtectedSecret = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    EnrolledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastUsedTimeStep = table.Column<long>(type: "bigint", nullable: false),
                    LastUsedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAuthenticators", x => x.UserSid);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserAuthenticators_AccountName",
                schema: "intranet",
                table: "UserAuthenticators",
                column: "AccountName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserAuthenticators",
                schema: "intranet");
        }
    }
}

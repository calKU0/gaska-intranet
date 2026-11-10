using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gaska.Intranet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGlpiConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GlpiConnections",
                schema: "intranet",
                columns: table => new
                {
                    UserSid = table.Column<string>(type: "nvarchar(184)", maxLength: 184, nullable: false),
                    AccountName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    GlpiUserId = table.Column<int>(type: "int", nullable: true),
                    ProtectedTokens = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    AccessTokenExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConnectedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlpiConnections", x => x.UserSid);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GlpiConnections",
                schema: "intranet");
        }
    }
}

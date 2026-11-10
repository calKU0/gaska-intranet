using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gaska.Intranet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveHiddenEmployees : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HiddenEmployees",
                schema: "intranet");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HiddenEmployees",
                schema: "intranet",
                columns: table => new
                {
                    EmployeeId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AccountName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    HiddenAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    HiddenBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HiddenEmployees", x => x.EmployeeId);
                });
        }
    }
}

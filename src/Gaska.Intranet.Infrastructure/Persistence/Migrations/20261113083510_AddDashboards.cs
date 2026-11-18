using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gaska.Intranet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDashboards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Dashboards",
                schema: "intranet",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OwnerSid = table.Column<string>(type: "nvarchar(184)", maxLength: 184, nullable: false),
                    OwnerAccount = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dashboards", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DashboardWidgets",
                schema: "intranet",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DashboardId = table.Column<int>(type: "int", nullable: false),
                    MetricKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Chart = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Period = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CustomFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    CustomTo = table.Column<DateOnly>(type: "date", nullable: true),
                    Granularity = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Width = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ComparePrevious = table.Column<bool>(type: "bit", nullable: false),
                    Top = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DashboardWidgets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DashboardWidgets_Dashboards_DashboardId",
                        column: x => x.DashboardId,
                        principalSchema: "intranet",
                        principalTable: "Dashboards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_OwnerSid",
                schema: "intranet",
                table: "Dashboards",
                column: "OwnerSid");

            migrationBuilder.CreateIndex(
                name: "IX_DashboardWidgets_DashboardId",
                schema: "intranet",
                table: "DashboardWidgets",
                column: "DashboardId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DashboardWidgets",
                schema: "intranet");

            migrationBuilder.DropTable(
                name: "Dashboards",
                schema: "intranet");
        }
    }
}

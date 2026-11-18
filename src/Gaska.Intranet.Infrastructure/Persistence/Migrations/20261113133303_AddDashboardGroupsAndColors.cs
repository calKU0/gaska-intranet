using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gaska.Intranet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDashboardGroupsAndColors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Color",
                schema: "intranet",
                table: "DashboardWidgets",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                schema: "intranet",
                table: "DashboardWidgets",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                schema: "intranet",
                table: "DashboardWidgets",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Color",
                schema: "intranet",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "Description",
                schema: "intranet",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "intranet",
                table: "DashboardWidgets");
        }
    }
}

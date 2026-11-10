using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gaska.Intranet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddModuleNetworkRestriction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "InternalNetworkOnly",
                schema: "intranet",
                table: "ModuleSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InternalNetworkOnly",
                schema: "intranet",
                table: "ModuleSettings");
        }
    }
}

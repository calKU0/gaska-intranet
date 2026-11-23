using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gaska.Intranet.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The "Klienci najdłużej bez kontaktu" chart was removed – charts of it saved on users' dashboards would only show an
    /// empty card, so they go too. Data only; the schema does not change.
    /// </summary>
    public partial class RemoveNoContactChart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM [intranet].[DashboardWidgets] WHERE [MetricKey] = N'crm.no-contact';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The deleted charts cannot be restored (and the chart no longer exists).
        }
    }
}

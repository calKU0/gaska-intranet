using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Gaska.Intranet.Infrastructure.Persistence;

/// <summary>Used only by the "dotnet ef" tool when creating migrations.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<IntranetDbContext>
{
    public IntranetDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<IntranetDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\MSSQLLocalDB;Database=GaskaIntranet;Trusted_Connection=True",
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", IntranetDbContext.Schema))
            .Options;

        return new IntranetDbContext(options);
    }
}

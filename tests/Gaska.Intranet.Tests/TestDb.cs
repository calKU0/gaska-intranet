using Gaska.Intranet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Gaska.Intranet.Tests;

/// <summary>In-memory SQLite database – a real SQL engine (supports ExecuteUpdate) without a server.</summary>
internal sealed class TestDb : IDbContextFactory<IntranetDbContext>, IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DbContextOptions<IntranetDbContext> _options;

    public TestDb()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<IntranetDbContext>().UseSqlite(_connection).Options;
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public IntranetDbContext CreateDbContext() => new(_options);

    public void Dispose() => _connection.Dispose();
}

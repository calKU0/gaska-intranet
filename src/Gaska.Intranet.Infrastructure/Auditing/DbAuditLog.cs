using Gaska.Intranet.Core.Auditing;
using Gaska.Intranet.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Gaska.Intranet.Infrastructure.Auditing;

internal sealed class DbAuditLog(
    IDbContextFactory<IntranetDbContext> dbFactory,
    IHttpContextAccessor httpContextAccessor,
    TimeProvider clock,
    ILogger<DbAuditLog> logger) : IAuditLog
{
    public async Task WriteAsync(string eventType, string? accountName, string? details = null, CancellationToken ct = default)
    {
        var entry = new AuditEntry
        {
            TimestampUtc = clock.GetUtcNow().UtcDateTime,
            EventType = eventType,
            AccountName = accountName,
            Details = details is { Length: > 1024 } ? details[..1024] : details,
            IpAddress = httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString(),
        };

        logger.LogInformation("Audyt {Event} {Account} {Details} {Ip}", eventType, accountName, details, entry.IpAddress);

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.AuditEntries.Add(entry);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // A failure to write the audit log must not block logins – the event is in the file log anyway.
            logger.LogError(ex, "Failed to write audit entry {Event}", eventType);
        }
    }

    public async Task<IReadOnlyList<AuditEntry>> GetRecentAsync(int take = 500, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AuditEntries
            .AsNoTracking()
            .OrderByDescending(e => e.Id)
            .Take(take)
            .ToListAsync(ct);
    }
}

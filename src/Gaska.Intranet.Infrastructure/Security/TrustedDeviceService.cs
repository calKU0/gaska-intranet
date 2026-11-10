using System.Security.Cryptography;
using System.Text;
using Gaska.Intranet.Core.Auditing;
using Gaska.Intranet.Core.Organization;
using Gaska.Intranet.Core.Security;
using Gaska.Intranet.Infrastructure.Persistence;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Infrastructure.Security;

/// <summary>
/// The device token has the form "{id}.{secret}". The id locates the record, the 256-bit secret is
/// compared with the hash in the database. The secret is rotated on every second-factor confirmation.
/// </summary>
internal sealed class TrustedDeviceService(
    IDbContextFactory<IntranetDbContext> dbFactory,
    IOptions<TwoFactorOptions> options,
    IAuditLog audit,
    TimeProvider clock) : ITrustedDeviceService
{
    public bool IsEnabled => options.Value.TrustedDeviceDays > 0;

    public TimeSpan Lifetime => TimeSpan.FromDays(options.Value.TrustedDeviceDays);

    public async Task<bool> IsTrustedAsync(DirectoryUser user, string? deviceToken, ClientInfo client, CancellationToken ct = default)
    {
        if (!IsEnabled || !TryParse(deviceToken, out var id, out var secret))
        {
            return false;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var device = await db.TrustedDevices.FindAsync([id], ct);

        var trusted = device is not null
            && Matches(device, user.AccountName, secret, now)
            && NetworkAddress.IsSameNetwork(device.IpAddress, client.IpAddress)
            && (user.PasswordLastSetUtc is null || device.VerifiedAtUtc >= user.PasswordLastSetUtc);

        if (trusted)
        {
            device!.LastUsedAtUtc = now;
            await db.SaveChangesAsync(ct);
        }

        return trusted;
    }

    public async Task<string> TrustAsync(DirectoryUser user, string? existingToken, ClientInfo client, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Same device (e.g. after an IP change) – update the record instead of creating a new one.
        TrustedDevice? device = null;
        if (TryParse(existingToken, out var id, out var oldSecret))
        {
            device = await db.TrustedDevices.FindAsync([id], ct);
            if (device is not null && !Matches(device, user.AccountName, oldSecret, now))
            {
                device = null;
            }
        }

        if (device is null)
        {
            device = new TrustedDevice { Id = Guid.NewGuid(), AccountName = user.AccountName, CreatedAtUtc = now };
            db.TrustedDevices.Add(device);
        }

        var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        device.TokenHash = Hash(secret);
        device.IpAddress = client.IpAddress;
        device.UserAgent = Truncate(client.UserAgent, 256);
        device.VerifiedAtUtc = now;
        device.ExpiresAtUtc = now.Add(Lifetime);

        await db.TrustedDevices
            .Where(d => d.ExpiresAtUtc < now.AddDays(-30) || d.RevokedAtUtc < now.AddDays(-30))
            .ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(AuditEvents.DeviceTrusted, user.AccountName, $"IP {client.IpAddress}, do {device.ExpiresAtUtc:yyyy-MM-dd HH:mm} UTC", ct);
        return $"{device.Id:N}.{secret}";
    }

    public async Task ForgetAsync(string? deviceToken, CancellationToken ct = default)
    {
        if (!TryParse(deviceToken, out var id, out var secret))
        {
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var device = await db.TrustedDevices.FindAsync([id], ct);
        if (device is null || !Matches(device, device.AccountName, secret, now))
        {
            return;
        }

        device.RevokedAtUtc = now;
        device.RevokedBy = device.AccountName;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(AuditEvents.DeviceForgotten, device.AccountName, $"IP {device.IpAddress}", ct);
    }

    public async Task<IReadOnlyList<TrustedDevice>> GetActiveAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TrustedDevices
            .AsNoTracking()
            .Where(d => d.RevokedAtUtc == null && d.ExpiresAtUtc > now)
            .OrderBy(d => d.AccountName)
            .ThenByDescending(d => d.VerifiedAtUtc)
            .ToListAsync(ct);
    }

    public async Task RevokeAsync(Guid id, string revokedBy, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var device = await db.TrustedDevices.FindAsync([id], ct);
        if (device is null || device.RevokedAtUtc is not null)
        {
            return;
        }

        device.RevokedAtUtc = clock.GetUtcNow().UtcDateTime;
        device.RevokedBy = revokedBy;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(AuditEvents.DeviceRevoked, revokedBy, $"{device.AccountName}, IP {device.IpAddress}", ct);
    }

    public async Task<int> RevokeAllAsync(string accountName, string revokedBy, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var count = await db.TrustedDevices
            .Where(d => d.AccountName == accountName && d.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.RevokedAtUtc, now)
                .SetProperty(d => d.RevokedBy, revokedBy), ct);

        if (count > 0)
        {
            await audit.WriteAsync(AuditEvents.DeviceRevoked, revokedBy, $"{accountName}: wszystkie ({count})", ct);
        }

        return count;
    }

    private static bool Matches(TrustedDevice device, string accountName, string secret, DateTime now) =>
        device.RevokedAtUtc is null
        && device.ExpiresAtUtc > now
        && string.Equals(device.AccountName, accountName, StringComparison.OrdinalIgnoreCase)
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(device.TokenHash), Encoding.ASCII.GetBytes(Hash(secret)));

    private static bool TryParse(string? token, out Guid id, out string secret)
    {
        id = Guid.Empty;
        secret = string.Empty;
        var dot = token?.IndexOf('.') ?? -1;
        if (dot <= 0 || !Guid.TryParseExact(token![..dot], "N", out id))
        {
            return false;
        }

        secret = token[(dot + 1)..];
        return secret.Length >= 32;
    }

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string? Truncate(string? value, int max) => value is { Length: > 0 } v && v.Length > max ? v[..max] : value;
}

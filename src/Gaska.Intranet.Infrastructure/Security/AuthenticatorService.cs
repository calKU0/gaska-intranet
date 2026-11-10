using Gaska.Intranet.Core.Auditing;
using Gaska.Intranet.Core.Organization;
using Gaska.Intranet.Core.Security;
using Gaska.Intranet.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Infrastructure.Security;

/// <summary>
/// Authenticator app (TOTP) management. Secrets are encrypted with ASP.NET Data Protection before they reach
/// the database, so a copy of the <c>UserAuthenticators</c> table alone is useless to an attacker.
/// </summary>
internal sealed class AuthenticatorService(
    IDbContextFactory<IntranetDbContext> dbFactory,
    IDataProtectionProvider dataProtection,
    IOptions<TwoFactorOptions> options,
    ITrustedDeviceService trustedDevices,
    IAuditLog audit,
    TimeProvider clock,
    ILogger<AuthenticatorService> logger) : IAuthenticatorService
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("Gaska.Intranet.Authenticator.v1");

    public async Task<bool> IsEnrolledAsync(DirectoryUser user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.UserAuthenticators.AnyAsync(a => a.UserSid == user.Sid, ct);
    }

    public AuthenticatorSetup CreateSetup(DirectoryUser user, string? existingKey = null)
    {
        var key = existingKey ?? Base32.Encode(Totp.GenerateSecret());
        return new AuthenticatorSetup(key, Totp.BuildOtpAuthUri(options.Value.AuthenticatorIssuer, user.AccountName, key, options.Value.AuthenticatorImageUrl));
    }

    public async Task<bool> EnrollAsync(DirectoryUser user, string secretKey, string code, CancellationToken ct = default)
    {
        var secret = Base32.Decode(secretKey);
        if (!Totp.TryVerify(secret, code, clock.GetUtcNow(), out var timeStep))
        {
            await audit.WriteAsync(AuditEvents.AuthenticatorInvalid, user.AccountName, "Konfiguracja aplikacji", ct);
            return false;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.UserAuthenticators.FindAsync([user.Sid], ct);
        if (entity is null)
        {
            entity = new UserAuthenticator { UserSid = user.Sid };
            db.UserAuthenticators.Add(entity);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        entity.AccountName = user.AccountName;
        entity.ProtectedSecret = _protector.Protect(secretKey);
        entity.EnrolledAtUtc = now;
        entity.LastUsedTimeStep = timeStep;
        entity.LastUsedAtUtc = now;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(AuditEvents.AuthenticatorEnrolled, user.AccountName, ct: ct);
        return true;
    }

    public async Task<bool> VerifyAsync(DirectoryUser user, string code, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.UserAuthenticators.FindAsync([user.Sid], ct);
        if (entity is null)
        {
            return false;
        }

        byte[] secret;
        try
        {
            secret = Base32.Decode(_protector.Unprotect(entity.ProtectedSecret));
        }
        catch (CryptographicException ex)
        {
            logger.LogError(ex,
                "Cannot decrypt the authenticator secret of {Account}: it was encrypted with a key this server cannot read " +
                "(another server sharing the database, or lost Data Protection keys). Reset the user's authenticator.",
                user.AccountName);
            throw new AuthenticatorUnreadableException(user.AccountName, ex);
        }

        if (!Totp.TryVerify(secret, code, clock.GetUtcNow(), out var timeStep) || timeStep <= entity.LastUsedTimeStep)
        {
            await audit.WriteAsync(AuditEvents.AuthenticatorInvalid, user.AccountName, ct: ct);
            return false;
        }

        // Conditional update – two parallel requests with the same code cannot both succeed.
        var updated = await db.UserAuthenticators
            .Where(a => a.UserSid == user.Sid && a.LastUsedTimeStep < timeStep)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.LastUsedTimeStep, timeStep)
                .SetProperty(a => a.LastUsedAtUtc, clock.GetUtcNow().UtcDateTime), ct);

        return updated == 1;
    }

    public async Task<IReadOnlyList<UserAuthenticator>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.UserAuthenticators.AsNoTracking().OrderBy(a => a.AccountName).ToListAsync(ct);
    }

    public async Task ResetAsync(string userSid, string resetBy, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.UserAuthenticators.FindAsync([userSid], ct);
        if (entity is null)
        {
            return;
        }

        db.UserAuthenticators.Remove(entity);
        await db.SaveChangesAsync(ct);

        // Without this, a trusted device could keep logging in without ever setting up the new app.
        await trustedDevices.RevokeAllAsync(entity.AccountName, resetBy, ct);
        await audit.WriteAsync(AuditEvents.AuthenticatorReset, resetBy, entity.AccountName, ct);
    }
}

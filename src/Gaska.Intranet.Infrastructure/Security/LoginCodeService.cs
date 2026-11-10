using System.Security.Cryptography;
using System.Text;
using Gaska.Intranet.Core.Organization;
using Gaska.Intranet.Core.Security;
using Gaska.Intranet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Infrastructure.Security;

internal sealed class LoginCodeService(
    IDbContextFactory<IntranetDbContext> dbFactory,
    IEnumerable<ILoginCodeSender> senders,
    IOptions<TwoFactorOptions> options,
    TimeProvider clock,
    ILogger<LoginCodeService> logger) : ILoginCodeService
{
    private readonly TwoFactorOptions _options = options.Value;
    private readonly IReadOnlyDictionary<DeliveryChannel, ILoginCodeSender> _senders =
        senders.ToDictionary(s => s.Channel);

    public IReadOnlyList<DeliveryOption> GetDeliveryOptions(DirectoryUser user) =>
        _senders.Values
            .OrderBy(s => s.Channel)
            .Select(s => (Sender: s, Destination: s.GetDestination(user)))
            .Where(x => x.Destination is not null)
            .Select(x => new DeliveryOption(x.Sender.Channel, x.Sender.MaskDestination(x.Destination!)))
            .ToList();

    public async Task<IssueCodeResult> IssueAsync(DirectoryUser user, DeliveryChannel channel, CancellationToken ct = default)
    {
        if (!_senders.TryGetValue(channel, out var sender) || sender.GetDestination(user) is not { } destination)
        {
            return new IssueCodeResult(IssueCodeStatus.ChannelUnavailable);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var recent = await db.LoginChallenges
            .Where(c => c.AccountName == user.AccountName && c.CreatedAtUtc > now.AddMinutes(-15))
            .Select(c => c.CreatedAtUtc)
            .ToListAsync(ct);

        if (recent.Count >= _options.MaxCodesPer15Minutes ||
            recent.Any(createdAt => createdAt > now.AddSeconds(-_options.ResendCooldownSeconds)))
        {
            return new IssueCodeResult(IssueCodeStatus.TooManyRequests);
        }

        // Previous unused codes expire – only the newest code is ever valid.
        await db.LoginChallenges
            .Where(c => c.AccountName == user.AccountName && c.CompletedAtUtc == null && c.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ExpiresAtUtc, now), ct);

        await db.LoginChallenges
            .Where(c => c.CreatedAtUtc < now.AddDays(-1))
            .ExecuteDeleteAsync(ct);

        var code = GenerateCode(_options.CodeLength);
        var challenge = new LoginChallenge
        {
            Id = Guid.NewGuid(),
            AccountName = user.AccountName,
            Channel = channel,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(_options.CodeLifetimeMinutes),
        };
        challenge.CodeHash = Hash(challenge.Id, code);

        db.LoginChallenges.Add(challenge);
        await db.SaveChangesAsync(ct);

        try
        {
            await sender.SendAsync(destination, code, TimeSpan.FromMinutes(_options.CodeLifetimeMinutes), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send code ({Channel}) to {User}", channel, user.AccountName);
            challenge.ExpiresAtUtc = now;
            await db.SaveChangesAsync(CancellationToken.None);
            return new IssueCodeResult(IssueCodeStatus.DeliveryFailed);
        }

        return new IssueCodeResult(IssueCodeStatus.Sent, challenge.Id, sender.MaskDestination(destination));
    }

    public async Task<VerifyCodeStatus> VerifyAsync(Guid challengeId, string accountName, string code, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var challenge = await db.LoginChallenges
            .FirstOrDefaultAsync(c => c.Id == challengeId && c.AccountName == accountName, ct);

        if (challenge is null || challenge.CompletedAtUtc is not null)
        {
            return VerifyCodeStatus.NotFound;
        }

        if (challenge.FailedAttempts >= _options.MaxAttempts)
        {
            return VerifyCodeStatus.TooManyAttempts;
        }

        if (challenge.ExpiresAtUtc <= now)
        {
            return VerifyCodeStatus.Expired;
        }

        var normalized = new string((code ?? string.Empty).Where(char.IsDigit).ToArray());
        if (!FixedTimeEquals(challenge.CodeHash, Hash(challenge.Id, normalized)))
        {
            challenge.FailedAttempts++;
            await db.SaveChangesAsync(ct);
            return challenge.FailedAttempts >= _options.MaxAttempts
                ? VerifyCodeStatus.TooManyAttempts
                : VerifyCodeStatus.InvalidCode;
        }

        // Conditional update prevents the same code from being used twice (parallel requests).
        var completed = await db.LoginChallenges
            .Where(c => c.Id == challenge.Id && c.CompletedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.CompletedAtUtc, now), ct);

        return completed == 1 ? VerifyCodeStatus.Success : VerifyCodeStatus.NotFound;
    }

    internal static string GenerateCode(int length)
    {
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            sb.Append((char)('0' + RandomNumberGenerator.GetInt32(10)));
        }

        return sb.ToString();
    }

    private static string Hash(Guid challengeId, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{challengeId:N}:{code}")));

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
}

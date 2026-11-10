using Gaska.Intranet.Core.Organization;

namespace Gaska.Intranet.Core.Security;

/// <summary>Delivery channel of a one-time login code.</summary>
public enum DeliveryChannel
{
    Sms = 1,
    Email = 2,
}

/// <summary>One-time login code (second factor). Only a hash of the code is stored.</summary>
public class LoginChallenge
{
    public Guid Id { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public DeliveryChannel Channel { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public int FailedAttempts { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

/// <summary>Available code delivery options for a user (with a masked destination).</summary>
public sealed record DeliveryOption(DeliveryChannel Channel, string MaskedDestination);

public enum IssueCodeStatus
{
    Sent,
    ChannelUnavailable,
    TooManyRequests,
    DeliveryFailed,
}

public sealed record IssueCodeResult(IssueCodeStatus Status, Guid? ChallengeId = null, string? MaskedDestination = null)
{
    public bool Succeeded => Status == IssueCodeStatus.Sent;
}

public enum VerifyCodeStatus
{
    Success,
    InvalidCode,
    Expired,
    TooManyAttempts,
    NotFound,
}

/// <summary>Issues and verifies one-time login codes.</summary>
public interface ILoginCodeService
{
    IReadOnlyList<DeliveryOption> GetDeliveryOptions(DirectoryUser user);

    Task<IssueCodeResult> IssueAsync(DirectoryUser user, DeliveryChannel channel, CancellationToken ct = default);

    Task<VerifyCodeStatus> VerifyAsync(Guid challengeId, string accountName, string code, CancellationToken ct = default);
}

/// <summary>
/// Limits failed login attempts – per account (password guessing)
/// and per IP address (many accounts tried from one address, a.k.a. password spraying).
/// </summary>
public interface ILoginThrottle
{
    bool IsLockedOut(string userName, string ipAddress);

    void RegisterFailure(string userName, string ipAddress);

    void Reset(string userName);
}

/// <summary>Sends a code through one channel (SMS, e-mail, in the future e.g. Teams).</summary>
public interface ILoginCodeSender
{
    DeliveryChannel Channel { get; }

    /// <summary>The user's destination for this channel, or <c>null</c> when AD has no data.</summary>
    string? GetDestination(DirectoryUser user);

    string MaskDestination(string destination);

    Task SendAsync(string destination, string code, TimeSpan validFor, CancellationToken ct = default);
}

using Gaska.Intranet.Core.Organization;

namespace Gaska.Intranet.Core.Security;

/// <summary>
/// Device (browser) on which the user completed the second factor. For a configured period,
/// logins from this device and the same IP address do not require a code again.
/// Only a hash of the cookie token is stored in the database.
/// </summary>
public class TrustedDevice
{
    public Guid Id { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string? UserAgent { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Last second-factor confirmation (a new one is required after a password change in AD).</summary>
    public DateTime VerifiedAtUtc { get; set; }

    public DateTime? LastUsedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? RevokedBy { get; set; }
}

/// <summary>Where the login comes from.</summary>
public sealed record ClientInfo(string IpAddress, string? UserAgent);

public interface ITrustedDeviceService
{
    /// <summary>Whether remembering devices is enabled (TrustedDeviceDays &gt; 0).</summary>
    bool IsEnabled { get; }

    TimeSpan Lifetime { get; }

    /// <summary>
    /// <c>true</c> when the token belongs to this account, has not expired or been revoked, comes from the same
    /// network and was confirmed after the last password change.
    /// </summary>
    Task<bool> IsTrustedAsync(DirectoryUser user, string? deviceToken, ClientInfo client, CancellationToken ct = default);

    /// <summary>Remembers the device after a successful second factor. Returns a new token (the previous one stops working).</summary>
    Task<string> TrustAsync(DirectoryUser user, string? existingToken, ClientInfo client, CancellationToken ct = default);

    Task ForgetAsync(string? deviceToken, CancellationToken ct = default);

    Task<IReadOnlyList<TrustedDevice>> GetActiveAsync(CancellationToken ct = default);

    Task RevokeAsync(Guid id, string revokedBy, CancellationToken ct = default);

    Task<int> RevokeAllAsync(string accountName, string revokedBy, CancellationToken ct = default);
}

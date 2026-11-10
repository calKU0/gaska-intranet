using Gaska.Intranet.Core.Organization;

namespace Gaska.Intranet.Core.Security;

/// <summary>
/// Authenticator app (TOTP) registered for a user. The secret is stored encrypted (ASP.NET Data Protection),
/// never in plain text. Keyed by the AD SID so renaming the account does not break it.
/// </summary>
public class UserAuthenticator
{
    public string UserSid { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string ProtectedSecret { get; set; } = string.Empty;
    public DateTime EnrolledAtUtc { get; set; }

    /// <summary>Time step of the last accepted code – the same code cannot be used twice.</summary>
    public long LastUsedTimeStep { get; set; }

    public DateTime? LastUsedAtUtc { get; set; }
}

/// <summary>Data shown to the user while setting up the app (QR code + key for manual entry).</summary>
public sealed record AuthenticatorSetup(string SecretKey, string OtpAuthUri)
{
    public string FormattedKey => Base32.FormatForDisplay(SecretKey);
}

public interface IAuthenticatorService
{
    Task<bool> IsEnrolledAsync(DirectoryUser user, CancellationToken ct = default);

    /// <summary>Creates a new secret, or rebuilds the setup for <paramref name="existingKey"/> (page reload).</summary>
    AuthenticatorSetup CreateSetup(DirectoryUser user, string? existingKey = null);

    /// <summary>Stores the secret once the user proves the app works by entering a valid code.</summary>
    Task<bool> EnrollAsync(DirectoryUser user, string secretKey, string code, CancellationToken ct = default);

    /// <summary>Verifies a login code (each code can be used only once).</summary>
    /// <exception cref="AuthenticatorUnreadableException">The stored secret cannot be decrypted on this server.</exception>
    Task<bool> VerifyAsync(DirectoryUser user, string code, CancellationToken ct = default);

    Task<IReadOnlyList<UserAuthenticator>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Removes the app (e.g. lost phone) – the user sets it up again at the next login.</summary>
    Task ResetAsync(string userSid, string resetBy, CancellationToken ct = default);
}

/// <summary>
/// The stored authenticator secret cannot be decrypted here – it was encrypted with keys of another server
/// (e.g. a developer machine and the production server sharing one database, with DPAPI-protected keys).
/// Only an administrator's reset (the user sets the app up again) helps.
/// </summary>
public sealed class AuthenticatorUnreadableException(string accountName, Exception inner)
    : Exception($"The authenticator secret of {accountName} cannot be decrypted on this server.", inner);

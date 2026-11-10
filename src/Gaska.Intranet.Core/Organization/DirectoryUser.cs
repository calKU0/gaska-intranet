namespace Gaska.Intranet.Core.Organization;

/// <summary>
/// Domain user account – result of authentication or a refresh from AD.
/// </summary>
public sealed record DirectoryUser
{
    public required string AccountName { get; init; }
    public required string Sid { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? FirstName { get; init; }
    public string? Email { get; init; }
    public string? MobilePhone { get; init; }

    /// <summary>Last password change – trusted devices become invalid after a password change.</summary>
    public DateTime? PasswordLastSetUtc { get; init; }

    /// <summary>SIDs of all security groups (including nested ones).</summary>
    public IReadOnlyCollection<string> GroupSids { get; init; } = [];

    public bool IsAdministrator { get; init; }
}

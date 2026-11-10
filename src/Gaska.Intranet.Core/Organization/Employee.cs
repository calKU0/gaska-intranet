namespace Gaska.Intranet.Core.Organization;

/// <summary>
/// Employee shown in the address book (data comes from Active Directory).
/// </summary>
public sealed record Employee
{
    public required string Id { get; init; }
    public required string AccountName { get; init; }
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Department { get; init; }
    public string? JobTitle { get; init; }
    public string? MobilePhone { get; init; }
    public string? Extension { get; init; }
    public string? Email { get; init; }

    public string FullName =>
        string.IsNullOrWhiteSpace(FirstName) && string.IsNullOrWhiteSpace(LastName)
            ? DisplayName
            : $"{FirstName} {LastName}".Trim();

    public string Initials =>
        string.Concat(new[] { FirstName, LastName }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => char.ToUpperInvariant(p[0])));
}

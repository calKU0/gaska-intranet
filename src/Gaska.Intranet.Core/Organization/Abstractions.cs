namespace Gaska.Intranet.Core.Organization;

/// <summary>Authentication and user account lookup in the directory (AD).</summary>
public interface IDirectoryAuthenticator
{
    /// <summary>
    /// Validates the user name and password. Returns the user, or <c>null</c> when the credentials are wrong
    /// or the account is disabled / locked out.
    /// </summary>
    Task<DirectoryUser?> ValidateCredentialsAsync(string userName, string password, CancellationToken ct = default);

    /// <summary>Reads the current data of an active account (no password) – e.g. to refresh groups.</summary>
    Task<DirectoryUser?> FindUserAsync(string accountName, CancellationToken ct = default);
}

/// <summary>Employee address book.</summary>
public interface IEmployeeDirectory
{
    Task<IReadOnlyList<Employee>> GetEmployeesAsync(CancellationToken ct = default);

    /// <summary>Forces a reload from AD on the next read.</summary>
    void InvalidateCache();
}

/// <summary>User and group search (admin panel).</summary>
public interface IPrincipalSearch
{
    Task<IReadOnlyList<DirectoryPrincipal>> SearchAsync(string term, int maxResults = 20, CancellationToken ct = default);
}

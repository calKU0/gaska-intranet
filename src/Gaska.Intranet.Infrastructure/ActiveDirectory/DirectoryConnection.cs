using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.Net;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using Gaska.Intranet.Core.Organization;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Infrastructure.ActiveDirectory;

/// <summary>
/// Single place that creates AD connections. On a domain-joined server it runs as the app pool account,
/// outside the domain – as the configured service account (<c>ActiveDirectory:ServiceAccount</c>).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DirectoryConnection(IOptions<ActiveDirectoryOptions> options)
{
    private readonly ActiveDirectoryOptions _options = options.Value;

    private NetworkCredential? ServiceCredential =>
        string.IsNullOrWhiteSpace(_options.ServiceAccount.UserName)
            ? null
            : new NetworkCredential(_options.ServiceAccount.UserName, _options.ServiceAccount.Password);

    /// <summary>
    /// Runs a directory query and turns low-level COM errors into <see cref="DirectoryUnavailableException"/>
    /// with a hint about the most common cause.
    /// </summary>
    public T Execute<T>(Func<T> query)
    {
        try
        {
            return query();
        }
        catch (Exception ex) when (ex is DirectoryServicesCOMException or COMException or System.DirectoryServices.AccountManagement.PrincipalException)
        {
            var hint = ServiceCredential is null
                ? " If this machine is not joined to the domain, set ActiveDirectory:ServiceAccount (read-only AD account) in appsettings.Local.json."
                : " Check the ActiveDirectory:ServiceAccount credentials.";
            throw new DirectoryUnavailableException($"Active Directory query failed: {ex.Message.Trim()}{hint}", ex);
        }
    }

    /// <summary>Context running as the service account / app pool account.</summary>
    public PrincipalContext CreateContext() => CreateContext(ServiceCredential);

    /// <summary>Context with the given credentials (e.g. the user signing in – reads their own data).</summary>
    public PrincipalContext CreateContext(NetworkCredential? credential)
    {
        var domain = string.IsNullOrWhiteSpace(_options.Domain) ? null : _options.Domain;
        return credential is null
            ? new PrincipalContext(ContextType.Domain, domain)
            : new PrincipalContext(ContextType.Domain, domain, credential.UserName, credential.Password);
    }

    /// <summary>Root of LDAP searches (empty <paramref name="distinguishedName"/> = the whole domain).</summary>
    public DirectoryEntry CreateEntry(string? distinguishedName)
    {
        var server = string.IsNullOrWhiteSpace(_options.Domain) ? null : _options.Domain;
        var dn = string.IsNullOrWhiteSpace(distinguishedName) ? DefaultNamingContext(server) : distinguishedName;
        return Entry(server is null ? $"LDAP://{dn}" : $"LDAP://{server}/{dn}");
    }

    private string DefaultNamingContext(string? server)
    {
        using var rootDse = Entry(server is null ? "LDAP://RootDSE" : $"LDAP://{server}/RootDSE");
        return rootDse.Properties["defaultNamingContext"].Value?.ToString()
            ?? throw new InvalidOperationException("Cannot read defaultNamingContext from AD.");
    }

    private DirectoryEntry Entry(string path) =>
        ServiceCredential is { } credential
            ? new DirectoryEntry(path, credential.UserName, credential.Password, AuthenticationTypes.Secure | AuthenticationTypes.Sealing | AuthenticationTypes.Signing)
            : new DirectoryEntry(path);
}

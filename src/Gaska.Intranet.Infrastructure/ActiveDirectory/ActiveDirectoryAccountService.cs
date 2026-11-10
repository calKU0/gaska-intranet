using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Gaska.Intranet.Core.Organization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Infrastructure.ActiveDirectory;

/// <summary>AD authentication and account/group search.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ActiveDirectoryAccountService(
    DirectoryConnection connection,
    IOptions<ActiveDirectoryOptions> options,
    IMemoryCache cache,
    ILogger<ActiveDirectoryAccountService> logger) : IDirectoryAuthenticator, IPrincipalSearch
{
    /// <summary>Aliases of built-in groups – independent of the language the domain was installed in.</summary>
    internal const string DomainAdminsAlias = "DomainAdmins";
    internal const string BuiltinAdministratorsAlias = "Administrators";

    private readonly ActiveDirectoryOptions _options = options.Value;

    public Task<DirectoryUser?> ValidateCredentialsAsync(string userName, string password, CancellationToken ct = default) =>
        Task.Run(() => ValidateCredentials(userName, password), ct);

    public Task<DirectoryUser?> FindUserAsync(string accountName, CancellationToken ct = default) =>
        Task.Run(() => Guard(accountName, () =>
        {
            using var context = connection.CreateContext();
            return LoadUser(context, accountName);
        }), ct);

    public Task<IReadOnlyList<DirectoryPrincipal>> SearchAsync(string term, int maxResults = 20, CancellationToken ct = default) =>
        Task.Run(() => connection.Execute(() => Search(term, maxResults)), ct);

    private DirectoryUser? ValidateCredentials(string userName, string password)
    {
        var accountName = StripDomain(userName.Trim());
        if (accountName.Length == 0 || string.IsNullOrEmpty(password))
        {
            return null;
        }

        return Guard(accountName, () =>
        {
            using (var context = connection.CreateContext())
            {
                if (!context.ValidateCredentials(accountName, password, ContextOptions.Negotiate | ContextOptions.Signing | ContextOptions.Sealing))
                {
                    return null;
                }
            }

            // The user's own data is read with their credentials – login works even without a service account.
            using var userContext = connection.CreateContext(new NetworkCredential(accountName, password));
            return LoadUser(userContext, accountName);
        });
    }

    private DirectoryUser? Guard(string accountName, Func<DirectoryUser?> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is PrincipalException or DirectoryServicesCOMException or COMException)
        {
            logger.LogError(ex, "Active Directory communication error ({User})", accountName);
            throw new DirectoryUnavailableException("Cannot connect to Active Directory.", ex);
        }
    }

    private DirectoryUser? LoadUser(PrincipalContext context, string accountName)
    {
        using var user = UserPrincipal.FindByIdentity(context, StripDomain(accountName));
        if (user is null || user.Enabled == false || user.IsAccountLockedOut() || user.Sid is null)
        {
            return null;
        }

        var entry = (DirectoryEntry)user.GetUnderlyingObject();
        entry.RefreshCache(["tokenGroups", _options.Attributes.MobilePhone, _options.Attributes.Email]);

        var groupSids = entry.Properties["tokenGroups"]
            .Cast<byte[]>()
            .Select(bytes => new SecurityIdentifier(bytes, 0).Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new DirectoryUser
        {
            AccountName = user.SamAccountName,
            Sid = user.Sid.Value,
            DisplayName = user.DisplayName ?? $"{user.GivenName} {user.Surname}".Trim(),
            FirstName = user.GivenName,
            Email = entry.Properties[_options.Attributes.Email].Value as string ?? user.EmailAddress,
            MobilePhone = entry.Properties[_options.Attributes.MobilePhone].Value as string,
            PasswordLastSetUtc = user.LastPasswordSet is { } set ? DateTime.SpecifyKind(set, DateTimeKind.Utc) : null,
            GroupSids = groupSids,
            IsAdministrator = GetAdministratorGroupSids(context, user.Sid.AccountDomainSid).Overlaps(groupSids),
        };
    }

    /// <summary>Resolves the configured administrator groups (names, SIDs, aliases) to SIDs.</summary>
    private HashSet<string> GetAdministratorGroupSids(PrincipalContext context, SecurityIdentifier? domainSid) =>
        cache.GetOrCreate("ad:admin-group-sids", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
            var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in _options.AdministratorGroups.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()))
            {
                if (ResolveAlias(name, domainSid) is { } sid)
                {
                    sids.Add(sid);
                    continue;
                }

                using var group = GroupPrincipal.FindByIdentity(context, name);
                if (group?.Sid is null)
                {
                    logger.LogWarning("Administrator group '{Group}' does not exist in AD", name);
                    continue;
                }

                sids.Add(group.Sid.Value);
            }

            return sids;
        })!;

    internal static string? ResolveAlias(string name, SecurityIdentifier? domainSid) => name switch
    {
        _ when name.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) => name,
        _ when name.Equals(BuiltinAdministratorsAlias, StringComparison.OrdinalIgnoreCase) =>
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
        _ when name.Equals(DomainAdminsAlias, StringComparison.OrdinalIgnoreCase) && domainSid is not null =>
            new SecurityIdentifier(WellKnownSidType.AccountDomainAdminsSid, domainSid).Value,
        _ => null,
    };

    private IReadOnlyList<DirectoryPrincipal> Search(string term, int maxResults)
    {
        term = term.Trim();
        if (term.Length < 2)
        {
            return [];
        }

        var t = Ldap.Escape(term);
        var filter =
            "(|" +
            $"(&{Ldap.EnabledUsersFilter}(|(sAMAccountName={t}*)(displayName=*{t}*)(givenName={t}*)(sn={t}*)))" +
            $"(&(objectCategory=group)(|(sAMAccountName=*{t}*)(cn=*{t}*)))" +
            ")";

        using var root = connection.CreateEntry(null);
        using var searcher = new DirectorySearcher(root, filter, ["objectSid", "sAMAccountName", "displayName", "cn", "objectClass"])
        {
            SizeLimit = maxResults,
        };

        using var results = searcher.FindAll();
        var list = new List<DirectoryPrincipal>();

        foreach (SearchResult result in results)
        {
            var sid = Ldap.GetSid(result);
            var account = Ldap.GetString(result, "sAMAccountName");
            if (sid is null || account is null)
            {
                continue;
            }

            var isGroup = result.Properties["objectClass"].Cast<object>()
                .Any(o => string.Equals(o.ToString(), "group", StringComparison.OrdinalIgnoreCase));

            list.Add(new DirectoryPrincipal(
                sid,
                account,
                Ldap.GetString(result, "displayName") ?? Ldap.GetString(result, "cn") ?? account,
                isGroup ? PrincipalKind.Group : PrincipalKind.User));
        }

        return list
            .OrderBy(p => p.Kind)
            .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>"EXAMPLE\jan" → "jan". UPN format ("jan@example.com") is left unchanged.</summary>
    internal static string StripDomain(string userName)
    {
        var slash = userName.IndexOf('\\');
        return slash >= 0 ? userName[(slash + 1)..] : userName;
    }
}

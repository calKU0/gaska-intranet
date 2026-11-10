using System.DirectoryServices;
using System.Runtime.Versioning;
using Gaska.Intranet.Core.Organization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Infrastructure.ActiveDirectory;

/// <summary>Employee list loaded from AD and kept in the memory cache.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ActiveDirectoryEmployeeDirectory(
    DirectoryConnection connection,
    IOptions<ActiveDirectoryOptions> options,
    IMemoryCache cache,
    ILogger<ActiveDirectoryEmployeeDirectory> logger) : IEmployeeDirectory
{
    private const string CacheKey = "ad:employees";
    private static readonly SemaphoreSlim LoadLock = new(1, 1);
    private readonly ActiveDirectoryOptions _options = options.Value;

    public async Task<IReadOnlyList<Employee>> GetEmployeesAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyList<Employee>? cached) && cached is not null)
        {
            return cached;
        }

        await LoadLock.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(CacheKey, out cached) && cached is not null)
            {
                return cached;
            }

            var employees = await Task.Run(() => connection.Execute(Load), ct);
            cache.Set(CacheKey, employees, TimeSpan.FromMinutes(_options.EmployeesCacheMinutes));
            return employees;
        }
        finally
        {
            LoadLock.Release();
        }
    }

    public void InvalidateCache() => cache.Remove(CacheKey);

    private IReadOnlyList<Employee> Load()
    {
        var map = _options.Attributes;
        var filter = $"(&{Ldap.EnabledUsersFilter}{_options.EmployeesExtraFilter}{GroupFilter()})";
        var exclusions = new EmployeeFilter(_options.EmployeesExcludedAccounts, _options.EmployeesExcludedContainers);
        string[] attributes =
        [
            "objectGUID", "sAMAccountName", "givenName", "sn", "displayName", "distinguishedName",
            map.Department, map.JobTitle, map.MobilePhone, map.Extension, map.Email,
        ];

        using var root = connection.CreateEntry(_options.EmployeesSearchBase);
        using var searcher = new DirectorySearcher(root, filter, attributes)
        {
            PageSize = 500,
            SearchScope = SearchScope.Subtree,
        };

        using var results = searcher.FindAll();
        var employees = new List<Employee>();
        var skipped = 0;

        foreach (SearchResult r in results)
        {
            var id = Ldap.GetGuid(r);
            var account = Ldap.GetString(r, "sAMAccountName");
            if (id is null || account is null)
            {
                continue;
            }

            // Technical accounts (ERPSYNC, ExtConsult, svc-…) configured in ActiveDirectory:EmployeesExcluded*.
            if (exclusions.IsExcluded(account, Ldap.GetString(r, "distinguishedName")))
            {
                skipped++;
                continue;
            }

            employees.Add(new Employee
            {
                Id = id,
                AccountName = account,
                FirstName = Ldap.GetString(r, "givenName") ?? string.Empty,
                LastName = Ldap.GetString(r, "sn") ?? string.Empty,
                DisplayName = Ldap.GetString(r, "displayName") ?? account,
                Department = Ldap.GetString(r, map.Department),
                JobTitle = Ldap.GetString(r, map.JobTitle),
                MobilePhone = Ldap.GetString(r, map.MobilePhone),
                Extension = Ldap.GetString(r, map.Extension),
                Email = Ldap.GetString(r, map.Email),
            });
        }

        logger.LogInformation("Loaded {Count} employees from Active Directory ({Skipped} technical accounts left out)", employees.Count, skipped);

        return employees
            .OrderBy(e => e.LastName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.FirstName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// "Only members of group X" (nested membership included, LDAP_MATCHING_RULE_IN_CHAIN). The group may be given by
    /// name – its DN is looked up once per load. Unknown group → no filter, logged as a warning (better a longer list
    /// than an empty one).
    /// </summary>
    private string GroupFilter()
    {
        var group = _options.EmployeesGroup?.Trim();
        if (string.IsNullOrEmpty(group))
        {
            return string.Empty;
        }

        var dn = group.Contains('=', StringComparison.Ordinal) ? group : FindGroupDn(group);
        if (dn is null)
        {
            logger.LogWarning("Employees group {Group} not found in Active Directory – the address book is not limited to it.", group);
            return string.Empty;
        }

        return $"(memberOf:1.2.840.113556.1.4.1941:={Ldap.Escape(dn)})";
    }

    private string? FindGroupDn(string name)
    {
        using var root = connection.CreateEntry(null);
        var escaped = Ldap.Escape(name);
        using var searcher = new DirectorySearcher(root, $"(&(objectCategory=group)(|(sAMAccountName={escaped})(cn={escaped})))",
            ["distinguishedName"]) { SearchScope = SearchScope.Subtree };
        return searcher.FindOne() is { } result ? Ldap.GetString(result, "distinguishedName") : null;
    }
}

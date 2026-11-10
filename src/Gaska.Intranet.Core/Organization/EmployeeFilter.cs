namespace Gaska.Intranet.Core.Organization;

/// <summary>
/// Rules that keep technical accounts (ERPSYNC, ExtConsult, svc-…, Exchange system mailboxes) out of the address book.
/// </summary>
public sealed class EmployeeFilter(IEnumerable<string> excludedAccounts, IEnumerable<string> excludedContainers)
{
    private readonly string[] _accounts = excludedAccounts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToArray();
    private readonly string[] _containers = excludedContainers.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToArray();

    /// <param name="accountName">sAMAccountName, e.g. "ERPSYNC".</param>
    /// <param name="distinguishedName">e.g. "CN=API ERP XL,OU=Konta serwisowe,DC=gaska,DC=local".</param>
    public bool IsExcluded(string accountName, string? distinguishedName) =>
        _accounts.Any(pattern => Matches(accountName, pattern)) ||
        (distinguishedName is not null &&
         _containers.Any(container => distinguishedName.Contains(container, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Case-insensitive wildcard match: "*" = any text, "?" = one character ("svc-*", "*$", "HealthMailbox*").</summary>
    public static bool Matches(string value, string pattern)
    {
        int v = 0, p = 0, star = -1, mark = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(value[v])))
            {
                v++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = v;
            }
            else if (star >= 0)
            {
                p = star + 1;
                v = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}

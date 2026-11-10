using System.DirectoryServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

namespace Gaska.Intranet.Infrastructure.ActiveDirectory;

[SupportedOSPlatform("windows")]
internal static class Ldap
{
    /// <summary>User accounts (no computers) that are not disabled.</summary>
    public const string EnabledUsersFilter =
        "(objectCategory=person)(objectClass=user)(!(userAccountControl:1.2.840.113556.1.4.803:=2))";

    /// <summary>Escapes a value for an LDAP filter (RFC 4515).</summary>
    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                '\\' => @"\5c",
                '*' => @"\2a",
                '(' => @"\28",
                ')' => @"\29",
                '\0' => @"\00",
                _ => c.ToString(),
            });
        }

        return sb.ToString();
    }

    public static string? GetString(SearchResult result, string attribute)
    {
        var values = result.Properties[attribute];
        return values.Count > 0 ? values[0]?.ToString()?.Trim() : null;
    }

    public static string? GetSid(SearchResult result)
    {
        var values = result.Properties["objectSid"];
        return values.Count > 0 && values[0] is byte[] bytes
            ? new SecurityIdentifier(bytes, 0).Value
            : null;
    }

    public static string? GetGuid(SearchResult result)
    {
        var values = result.Properties["objectGUID"];
        return values.Count > 0 && values[0] is byte[] bytes ? new Guid(bytes).ToString() : null;
    }
}

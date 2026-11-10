using System.Security.Claims;
using Gaska.Intranet.Core.Organization;

namespace Gaska.Intranet.Core.Security;

/// <summary>Claim types and roles used by the application.</summary>
public static class IntranetClaims
{
    public const string AdministratorRole = "Administrator";
    public const string DisplayName = "gaska:display_name";
    public const string FirstName = ClaimTypes.GivenName;
    public const string Sid = ClaimTypes.PrimarySid;
    public const string GroupSid = ClaimTypes.GroupSid;
    public const string RefreshedAt = "gaska:refreshed_at";
    public const string AuthenticatedAt = "gaska:auth_time";

    /// <summary>
    /// Claim added on every request (never stored in the cookie): whether the request came from the company network.
    /// </summary>
    public const string Network = "gaska:network";
    public const string InternalNetwork = "internal";

    /// <param name="authenticatedAt">Time of the full login (password + second factor) – basis for the absolute session limit.</param>
    public static ClaimsIdentity CreateIdentity(DirectoryUser user, string authenticationType, DateTimeOffset authenticatedAt)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.AccountName),
            new(ClaimTypes.NameIdentifier, user.Sid),
            new(Sid, user.Sid),
            new(DisplayName, string.IsNullOrWhiteSpace(user.DisplayName) ? user.AccountName : user.DisplayName),
            new(RefreshedAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
            new(AuthenticatedAt, authenticatedAt.ToUnixTimeSeconds().ToString()),
        };

        if (!string.IsNullOrWhiteSpace(user.FirstName))
        {
            claims.Add(new Claim(FirstName, user.FirstName));
        }

        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, user.Email));
        }

        claims.AddRange(user.GroupSids.Select(sid => new Claim(GroupSid, sid)));

        if (user.IsAdministrator)
        {
            claims.Add(new Claim(ClaimTypes.Role, AdministratorRole));
        }

        return new ClaimsIdentity(claims, authenticationType, ClaimTypes.Name, ClaimTypes.Role);
    }

    public static string GetDisplayName(this ClaimsPrincipal user) =>
        user.FindFirst(DisplayName)?.Value ?? user.Identity?.Name ?? string.Empty;

    public static string? GetFirstName(this ClaimsPrincipal user) => user.FindFirst(FirstName)?.Value;

    public static bool IsAdministrator(this ClaimsPrincipal user) => user.IsInRole(AdministratorRole);

    /// <summary>The account's SID (empty for an anonymous user).</summary>
    public static string GetSid(this ClaimsPrincipal user) => user.FindFirst(Sid)?.Value ?? string.Empty;

    /// <summary>The user's SID plus the SIDs of all their groups.</summary>
    public static IReadOnlySet<string> GetSecurityIdentifiers(this ClaimsPrincipal user)
    {
        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in user.Claims.Where(c => c.Type is Sid or GroupSid))
        {
            sids.Add(claim.Value);
        }

        return sids;
    }

    public static bool IsOnInternalNetwork(this ClaimsPrincipal user) =>
        user.HasClaim(Network, InternalNetwork);

    public static DateTimeOffset? GetRefreshedAt(this ClaimsPrincipal user) => GetTime(user, RefreshedAt);

    public static DateTimeOffset? GetAuthenticatedAt(this ClaimsPrincipal user) => GetTime(user, AuthenticatedAt);

    private static DateTimeOffset? GetTime(ClaimsPrincipal user, string claimType) =>
        long.TryParse(user.FindFirst(claimType)?.Value, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}

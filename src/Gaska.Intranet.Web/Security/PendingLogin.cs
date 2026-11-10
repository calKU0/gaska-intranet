using System.Security.Claims;
using System.Text.Json;
using Gaska.Intranet.Core.Organization;
using Gaska.Intranet.Core.Security;
using Microsoft.AspNetCore.Authentication;

namespace Gaska.Intranet.Web.Security;

/// <summary>
/// Login state between the password step and the final sign-in. Kept in a short-lived (10 min), encrypted cookie
/// together with the account data read from AD during password validation, so later steps do not query AD again.
/// </summary>
public sealed record PendingLoginState(
    DirectoryUser User,
    string ReturnUrl,
    Guid? ChallengeId = null,
    DeliveryChannel? Channel = null,
    string? MaskedDestination = null,
    bool IdentityConfirmed = false,
    string? AuthenticatorKey = null,
    bool RememberDevice = true)
{
    private const string UserClaim = "user";
    private const string ReturnUrlClaim = "return_url";
    private const string ChallengeClaim = "challenge_id";
    private const string ChannelClaim = "channel";
    private const string DestinationClaim = "destination";
    private const string IdentityConfirmedClaim = "identity_confirmed";
    private const string AuthenticatorKeyClaim = "authenticator_key";
    private const string RememberDeviceClaim = "remember_device";

    public static async Task<PendingLoginState?> ReadAsync(HttpContext http)
    {
        var result = await http.AuthenticateAsync(AuthSchemes.Pending);
        var principal = result.Principal;
        if (!result.Succeeded || principal?.FindFirst(UserClaim)?.Value is not { } json ||
            JsonSerializer.Deserialize<DirectoryUser>(json) is not { } user)
        {
            return null;
        }

        string? Value(string type) => principal.FindFirst(type)?.Value;

        return new PendingLoginState(
            user,
            Value(ReturnUrlClaim) ?? "/",
            Guid.TryParse(Value(ChallengeClaim), out var id) ? id : null,
            Enum.TryParse<DeliveryChannel>(Value(ChannelClaim), out var channel) ? channel : null,
            Value(DestinationClaim),
            Value(IdentityConfirmedClaim) == "true",
            Value(AuthenticatorKeyClaim),
            Value(RememberDeviceClaim) != "false");
    }

    public Task SaveAsync(HttpContext http)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, User.AccountName),
            new(UserClaim, JsonSerializer.Serialize(User)),
            new(ReturnUrlClaim, ReturnUrl),
            new(IdentityConfirmedClaim, IdentityConfirmed ? "true" : "false"),
            new(RememberDeviceClaim, RememberDevice ? "true" : "false"),
        };

        void Add(string type, string? value)
        {
            if (value is not null)
            {
                claims.Add(new Claim(type, value));
            }
        }

        Add(ChallengeClaim, ChallengeId?.ToString());
        Add(ChannelClaim, Channel?.ToString());
        Add(DestinationClaim, MaskedDestination);
        Add(AuthenticatorKeyClaim, AuthenticatorKey);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AuthSchemes.Pending));
        return http.SignInAsync(AuthSchemes.Pending, principal);
    }

    public static Task ClearAsync(HttpContext http) => http.SignOutAsync(AuthSchemes.Pending);

    /// <summary>Accepts local return URLs only (open redirect protection).</summary>
    public static string SanitizeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) &&
        returnUrl.StartsWith('/') &&
        !returnUrl.StartsWith("//", StringComparison.Ordinal) &&
        !returnUrl.StartsWith("/\\", StringComparison.Ordinal) &&
        !returnUrl.StartsWith("/konto/", StringComparison.OrdinalIgnoreCase)
            ? returnUrl
            : "/";
}

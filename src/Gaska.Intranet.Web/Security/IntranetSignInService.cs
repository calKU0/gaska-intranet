using System.Security.Claims;
using Gaska.Intranet.Core.Auditing;
using Gaska.Intranet.Core.Organization;
using Gaska.Intranet.Core.Security;
using Gaska.Intranet.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Web.Security;

/// <summary>
/// Login flow decisions, trusted devices, sign-in and sign-out – one place shared by all account pages.
/// <para>
/// Second factor: an authenticator app (TOTP) when the user has one; otherwise an SMS/e-mail code, after which
/// (when <see cref="TwoFactorOptions.RequireAuthenticatorApp"/> is on) the user must set up the app before getting in.
/// </para>
/// </summary>
public sealed class IntranetSignInService(
    ITrustedDeviceService trustedDevices,
    IAuthenticatorService authenticators,
    IAuditLog audit,
    Gaska.Intranet.Core.Helpdesk.IHelpdeskConnector helpdesk,
    IOptions<TwoFactorOptions> twoFactorOptions,
    IOptions<SessionOptions> sessionOptions,
    TimeProvider clock)
{
    public bool CanRememberDevice => trustedDevices.IsEnabled;

    public int RememberDeviceDays => (int)trustedDevices.Lifetime.TotalDays;

    private bool RequireAuthenticatorApp => twoFactorOptions.Value.RequireAuthenticatorApp;

    /// <summary>Second factor uses the authenticator app (instead of SMS/e-mail).</summary>
    public Task<bool> UsesAuthenticatorAsync(DirectoryUser user, CancellationToken ct = default) =>
        authenticators.IsEnrolledAsync(user, ct);

    /// <summary>The user confirmed identity with an SMS/e-mail code but still has to set up the app.</summary>
    public async Task<bool> MustSetUpAuthenticatorAsync(DirectoryUser user, CancellationToken ct = default) =>
        RequireAuthenticatorApp && !await authenticators.IsEnrolledAsync(user, ct);

    /// <summary>
    /// This browser (cookie) from this network has already completed the second factor. Never true for a user who
    /// still has to set up the app – a device trusted earlier must not let them skip the setup.
    /// </summary>
    public async Task<bool> IsTrustedDeviceAsync(HttpContext http, DirectoryUser user) =>
        !await MustSetUpAuthenticatorAsync(user, http.RequestAborted) &&
        await trustedDevices.IsTrustedAsync(user, TrustedDeviceCookie.Read(http), ClientNetwork.GetClientInfo(http), http.RequestAborted);

    /// <summary>Finishes the login after a successful second factor.</summary>
    public async Task CompleteSignInAsync(HttpContext http, DirectoryUser user, bool rememberDevice, string auditEvent, string? details = null)
    {
        if (rememberDevice && CanRememberDevice)
        {
            var token = await trustedDevices.TrustAsync(user, TrustedDeviceCookie.Read(http), ClientNetwork.GetClientInfo(http), http.RequestAborted);
            TrustedDeviceCookie.Write(http, token, trustedDevices.Lifetime);
        }

        await SignInAsync(http, user, auditEvent, details);
    }

    /// <summary>Creates the session (persistent cookie – survives closing the browser, expires after inactivity).</summary>
    public async Task SignInAsync(HttpContext http, DirectoryUser user, string auditEvent, string? details = null)
    {
        var now = clock.GetUtcNow();
        var principal = new ClaimsPrincipal(IntranetClaims.CreateIdentity(user, AuthSchemes.Main, now));

        await http.SignInAsync(AuthSchemes.Main, principal, new AuthenticationProperties
        {
            IsPersistent = true,
            IssuedUtc = now,
            ExpiresUtc = now.Add(sessionOptions.Value.IdleTimeout),
            AllowRefresh = true,
        });

        await PendingLoginState.ClearAsync(http);
        await audit.WriteAsync(auditEvent, user.AccountName, details, http.RequestAborted);

        // Login complete – keep the GLPI tokens requested at the password step (if GLPI accepted the password).
        helpdesk.CompleteConnect(user);
    }

    public async Task SignOutAsync(HttpContext http, bool forgetDevice)
    {
        var accountName = http.User.Identity?.Name;

        if (forgetDevice)
        {
            await trustedDevices.ForgetAsync(TrustedDeviceCookie.Read(http), http.RequestAborted);
            TrustedDeviceCookie.Delete(http);
        }

        await http.SignOutAsync(AuthSchemes.Main);
        await PendingLoginState.ClearAsync(http);
        await audit.WriteAsync(AuditEvents.Logout, accountName, forgetDevice ? "z zapomnieniem urządzenia" : null, http.RequestAborted);
    }
}

using Gaska.Intranet.Core.Auditing;
using Gaska.Intranet.Core.Modules;
using Gaska.Intranet.Core.Organization;
using Gaska.Intranet.Core.Security;
using Gaska.Intranet.Infrastructure;
using Gaska.Intranet.Web.Modules;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Web.Security;

public static class AuthenticationSetup
{
    public static IServiceCollection AddIntranetSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SessionOptions>().Bind(configuration.GetSection(SessionOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<WebSecurityOptions>().Bind(configuration.GetSection(WebSecurityOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<ClientNetwork>();
        services.AddScoped<IntranetSignInService>();

        // The __Host- prefix = HTTPS only, this host only (cannot be planted from a subdomain).
        services.AddAntiforgery(o =>
        {
            o.Cookie.Name = "__Host-Gaska.Af";
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        });

        services.AddAuthentication(AuthSchemes.Main)
            .AddCookie(AuthSchemes.Main, o =>
            {
                o.Cookie.Name = "__Host-Gaska.Session";
                o.Cookie.HttpOnly = true;
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.LoginPath = AccountRoutes.Login;
                o.LogoutPath = AccountRoutes.Logout;
                o.AccessDeniedPath = AccountRoutes.AccessDenied;
                o.ReturnUrlParameter = "returnUrl";
                o.SlidingExpiration = true;
                o.EventsType = typeof(SessionValidationEvents);
            })
            .AddCookie(AuthSchemes.Pending, o =>
            {
                o.Cookie.Name = "__Host-Gaska.Pending";
                o.Cookie.HttpOnly = true;
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.ExpireTimeSpan = TimeSpan.FromMinutes(10);
                o.SlidingExpiration = false;
                o.LoginPath = AccountRoutes.Login;
            });

        // Session idle timeout from configuration (Session:IdleTimeoutMinutes).
        services.AddOptions<CookieAuthenticationOptions>(AuthSchemes.Main)
            .Configure<IOptions<SessionOptions>>((cookie, session) => cookie.ExpireTimeSpan = session.Value.IdleTimeout);

        services.AddScoped<SessionValidationEvents>();

        services.AddAuthorizationBuilder()
            // Everything requires a login by default; exceptions are marked [AllowAnonymous].
            .SetFallbackPolicy(new AuthorizationPolicyBuilder(AuthSchemes.Main).RequireAuthenticatedUser().Build());

        services.AddSingleton<IAuthorizationPolicyProvider, ModulePolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, ModuleAccessHandler>();
        services.AddSingleton<IModuleCatalog, ModuleCatalog>();

        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, SessionRevalidatingStateProvider>();

        services.AddIntranetRateLimiting();

        return services;
    }
}

/// <summary>
/// Guards the session on every request:
/// <list type="bullet">
/// <item>maximum session length (Session:AbsoluteTimeoutHours) – then a new login is required,</item>
/// <item>renews the cookie on activity (idle time counted from the last request),</item>
/// <item>periodically refreshes groups from AD; an account disabled in AD is signed out.</item>
/// </list>
/// </summary>
internal sealed class SessionValidationEvents(
    IDirectoryAuthenticator directory,
    IOptions<ActiveDirectoryOptions> directoryOptions,
    IOptions<SessionOptions> sessionOptions,
    IAuditLog audit,
    TimeProvider clock,
    ILogger<SessionValidationEvents> logger) : CookieAuthenticationEvents
{
    private static readonly TimeSpan RenewAfter = TimeSpan.FromMinutes(1);

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        if (principal?.Identity?.Name is not { } accountName)
        {
            return;
        }

        var now = clock.GetUtcNow();
        var authenticatedAt = principal.GetAuthenticatedAt();

        if (authenticatedAt is null || now - authenticatedAt > sessionOptions.Value.AbsoluteTimeout)
        {
            await RejectAsync(context, AuditEvents.SessionExpired, accountName, "Przekroczony maksymalny czas sesji");
            return;
        }

        // Default sliding expiration only renews after half of the lifetime – renew more often
        // so that "60 minutes of inactivity" means exactly 60 minutes since the last activity.
        if (context.Properties.IssuedUtc is { } issued && now - issued > RenewAfter)
        {
            context.ShouldRenew = true;
        }

        var refreshedAt = principal.GetRefreshedAt();
        if (refreshedAt is not null && now - refreshedAt < TimeSpan.FromMinutes(directoryOptions.Value.SessionRefreshMinutes))
        {
            return;
        }

        DirectoryUser? user;
        try
        {
            user = await directory.FindUserAsync(accountName, context.HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            // AD temporarily unavailable – do not sign out, retry on the next request.
            logger.LogWarning(ex, "Failed to refresh session of {User}", accountName);
            return;
        }

        if (user is null)
        {
            await RejectAsync(context, AuditEvents.SessionRevoked, accountName, "Konto wyłączone/zablokowane w AD");
            return;
        }

        context.ReplacePrincipal(new(IntranetClaims.CreateIdentity(user, AuthSchemes.Main, authenticatedAt.Value)));
        context.ShouldRenew = true;
    }

    private async Task RejectAsync(CookieValidatePrincipalContext context, string auditEvent, string accountName, string reason)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(AuthSchemes.Main);
        await audit.WriteAsync(auditEvent, accountName, reason);
    }
}

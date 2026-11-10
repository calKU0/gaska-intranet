using System.Security.Claims;
using Gaska.Intranet.Core.Organization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace Gaska.Intranet.Web.Security;

/// <summary>
/// For open browser tabs (Blazor Server connection): every 30 minutes checks whether the account
/// is still active in AD. If not, the user is cut off from the application.
/// </summary>
internal sealed class SessionRevalidatingStateProvider(
    ILoggerFactory loggerFactory,
    IDirectoryAuthenticator directory) : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(30);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        if (authenticationState.User.Identity?.Name is not { } accountName)
        {
            return false;
        }

        try
        {
            return await directory.FindUserAsync(accountName, cancellationToken) is not null;
        }
        catch (Exception)
        {
            // An AD outage must not kick everyone out of the application.
            return true;
        }
    }
}

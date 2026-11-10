using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Gaska.Intranet.Web.Security;

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // The logout form carries an antiforgery token – a foreign site cannot log the user out.
        endpoints.MapPost(AccountRoutes.Logout, async (HttpContext http, IntranetSignInService signIn, [FromForm] bool forgetDevice = false) =>
        {
            await signIn.SignOutAsync(http, forgetDevice);
            return TypedResults.LocalRedirect(AccountRoutes.Login);
        })
        .WithMetadata(new RequireAntiforgeryTokenAttribute())
        .AllowAnonymous();

        // Called by session.js after user activity (mouse, keyboard) – extends the session,
        // because an open Blazor page talks over a WebSocket and never refreshes the cookie itself.
        endpoints.MapGet(AccountRoutes.SessionPing, (HttpContext http) =>
            TypedResults.Ok(new { authenticated = http.User.Identity?.IsAuthenticated == true }))
        .AllowAnonymous();

        return endpoints;
    }
}

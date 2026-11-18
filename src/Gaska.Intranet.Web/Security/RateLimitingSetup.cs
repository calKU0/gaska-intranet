using System.Threading.RateLimiting;
using Gaska.Intranet.Core.Auditing;
using Gaska.Intranet.Infrastructure;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Web.Security;

/// <summary>
/// Request limits: a general one (bots, scanners, DoS) and a stricter one for login forms. When exceeded the server
/// responds with 429 Too Many Requests.
/// <para>
/// The general limit counts a signed-in user's requests per account, others per IP address – behind a reverse proxy
/// every employee may come from the proxy's address and would share one limit. The interactive pages' own connection
/// (<c>/_blazor</c> – with long polling a request every few seconds per tab) and static files are not counted: cutting
/// them off breaks the open pages ("Brak połączenia z serwerem"). Requests from an address shared by many people
/// (a NAT firewall, "Security:SharedClientAddresses") have no per-IP limits - only the per-account ones.
/// </para>
/// </summary>
public static class RateLimitingSetup
{
    public static IServiceCollection AddIntranetRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            o.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(http =>
                {
                    if (IsAppConnectionOrStaticFile(http))
                    {
                        return RateLimitPartition.GetNoLimiter(string.Empty);
                    }

                    var limit = Options(http).RequestsPerMinutePerIp;
                    if (http.User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name })
                    {
                        return RateLimitPartition.GetFixedWindowLimiter("user:" + name, _ => PerMinute(limit));
                    }

                    return IsShared(http)
                        ? RateLimitPartition.GetNoLimiter(string.Empty)
                        : RateLimitPartition.GetFixedWindowLimiter("ip:" + IpOf(http), _ => PerMinute(limit));
                }),
                PartitionedRateLimiter.Create<HttpContext, string>(http =>
                {
                    if (!IsAccountFormPost(http) || IsShared(http))
                    {
                        return RateLimitPartition.GetNoLimiter(string.Empty);
                    }

                    var limit = Options(http).LoginAttemptsPerMinutePerIp;
                    return RateLimitPartition.GetFixedWindowLimiter("login:" + IpOf(http), _ => PerMinute(limit));
                }));

            o.OnRejected = async (context, ct) =>
            {
                var http = context.HttpContext;
                var audit = http.RequestServices.GetRequiredService<IAuditLog>();
                await audit.WriteAsync(AuditEvents.RateLimited, http.User.Identity?.Name, $"{http.Request.Method} {http.Request.Path}", ct);

                http.Response.Headers.RetryAfter = "60";
                http.Response.ContentType = "text/plain; charset=utf-8";
                await http.Response.WriteAsync("Zbyt wiele żądań. Spróbuj ponownie za minutę.", ct);
            };
        });

        return services;
    }

    /// <summary>The Blazor circuit (negotiate, WebSocket, long polling) and files of the page itself.</summary>
    private static bool IsAppConnectionOrStaticFile(HttpContext http)
    {
        var path = http.Request.Path;
        return path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/_framework", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/_content", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/css", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/js", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/img", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/manifest.json", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/sw.js", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAccountFormPost(HttpContext http) =>
        HttpMethods.IsPost(http.Request.Method) &&
        http.Request.Path.StartsWithSegments("/konto", StringComparison.OrdinalIgnoreCase);

    private static WebSecurityOptions Options(HttpContext http) =>
        http.RequestServices.GetRequiredService<IOptions<WebSecurityOptions>>().Value;

    private static bool IsShared(HttpContext http) =>
        http.RequestServices.GetRequiredService<IOptions<ClientAddressOptions>>().Value.IsShared(IpOf(http));

    private static string IpOf(HttpContext http) => ClientNetwork.GetIpAddress(http)?.ToString() ?? "unknown";

    private static FixedWindowRateLimiterOptions PerMinute(int permits) => new()
    {
        PermitLimit = permits,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    };
}

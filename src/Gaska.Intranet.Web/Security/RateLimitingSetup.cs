using System.Threading.RateLimiting;
using Gaska.Intranet.Core.Auditing;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Web.Security;

/// <summary>
/// Per-IP request limits: a general one (bots, scanners, DoS) and a stricter one for login forms.
/// When exceeded the server responds with 429 Too Many Requests.
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
                    var limit = Options(http).RequestsPerMinutePerIp;
                    return RateLimitPartition.GetFixedWindowLimiter("all:" + IpOf(http), _ => PerMinute(limit));
                }),
                PartitionedRateLimiter.Create<HttpContext, string>(http =>
                {
                    if (!IsAccountFormPost(http))
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

    private static bool IsAccountFormPost(HttpContext http) =>
        HttpMethods.IsPost(http.Request.Method) &&
        http.Request.Path.StartsWithSegments("/konto", StringComparison.OrdinalIgnoreCase);

    private static WebSecurityOptions Options(HttpContext http) =>
        http.RequestServices.GetRequiredService<IOptions<WebSecurityOptions>>().Value;

    private static string IpOf(HttpContext http) => ClientNetwork.GetIpAddress(http)?.ToString() ?? "unknown";

    private static FixedWindowRateLimiterOptions PerMinute(int permits) => new()
    {
        PermitLimit = permits,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    };
}

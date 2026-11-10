using System.Net;
using System.Security.Claims;
using Gaska.Intranet.Core.Security;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Web.Security;

/// <summary>Client address and detection whether the request came from the company network.</summary>
public sealed class ClientNetwork
{
    private readonly IReadOnlyList<IPNetwork> _internalNetworks;

    public ClientNetwork(IOptions<WebSecurityOptions> options)
    {
        _internalNetworks = options.Value.InternalNetworks
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => IPNetwork.Parse(n.Trim()))
            .ToList();
    }

    public static IPAddress? GetIpAddress(HttpContext http)
    {
        var ip = http.Connection.RemoteIpAddress;
        return ip is { IsIPv4MappedToIPv6: true } ? ip.MapToIPv4() : ip;
    }

    public static ClientInfo GetClientInfo(HttpContext http) =>
        new(GetIpAddress(http)?.ToString() ?? "unknown", http.Request.Headers.UserAgent.ToString());

    public bool IsInternal(IPAddress? ip) => ip is not null && _internalNetworks.Any(n => n.Contains(ip));
}

/// <summary>
/// Adds to the signed-in user (for this request only) the network the request came from.
/// The Blazor connection (WebSocket) also passes through this middleware, so the whole tab session gets it –
/// which also means a tab opened before connecting to the VPN keeps the old network until it is reloaded.
/// </summary>
public sealed class NetworkClaimMiddleware(RequestDelegate next, ClientNetwork network)
{
    public Task InvokeAsync(HttpContext http)
    {
        if (http.User.Identity?.IsAuthenticated == true && network.IsInternal(ClientNetwork.GetIpAddress(http)))
        {
            http.User.AddIdentity(new ClaimsIdentity([new Claim(IntranetClaims.Network, IntranetClaims.InternalNetwork)]));
        }

        return next(http);
    }
}

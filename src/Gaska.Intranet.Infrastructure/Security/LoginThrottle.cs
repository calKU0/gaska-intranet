using Gaska.Intranet.Core.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Infrastructure.Security;

/// <summary>
/// Lockout after several failed attempts – protects against password guessing, password spraying
/// (many accounts from one IP) and against outsiders locking out AD accounts.
/// </summary>
internal sealed class LoginThrottle(IMemoryCache cache, IOptions<TwoFactorOptions> options) : ILoginThrottle
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    public bool IsLockedOut(string userName, string ipAddress) =>
        Count(UserKey(userName)) >= options.Value.MaxPasswordFailuresPer15Minutes ||
        Count(IpKey(ipAddress)) >= options.Value.MaxPasswordFailuresPerIpPer15Minutes;

    public void RegisterFailure(string userName, string ipAddress)
    {
        Increment(UserKey(userName));
        Increment(IpKey(ipAddress));
    }

    public void Reset(string userName) => cache.Remove(UserKey(userName));

    private int Count(string key) => cache.TryGetValue(key, out int failures) ? failures : 0;

    private void Increment(string key) => cache.Set(key, Count(key) + 1, Window);

    private static string UserKey(string userName) =>
        "login-failures:user:" + userName.Trim().Split('\\').Last().ToLowerInvariant();

    private static string IpKey(string ipAddress) => "login-failures:ip:" + ipAddress;
}

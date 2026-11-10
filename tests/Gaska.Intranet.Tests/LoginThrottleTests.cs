using Gaska.Intranet.Infrastructure;
using Gaska.Intranet.Infrastructure.Security;
using Microsoft.Extensions.Caching.Memory;
using Options = Microsoft.Extensions.Options.Options;

namespace Gaska.Intranet.Tests;

public class LoginThrottleTests
{
    private readonly LoginThrottle _throttle = new(
        new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new TwoFactorOptions { MaxPasswordFailuresPer15Minutes = 3, MaxPasswordFailuresPerIpPer15Minutes = 5 }));

    [Fact]
    public void Locks_account_after_failures()
    {
        for (var i = 0; i < 3; i++)
        {
            _throttle.RegisterFailure(@"GASKA\jan", $"10.0.0.{i}");
        }

        Assert.True(_throttle.IsLockedOut("jan", "10.0.0.99"));
        Assert.False(_throttle.IsLockedOut("anna", "10.0.0.99"));
    }

    [Fact]
    public void Locks_ip_trying_many_accounts()
    {
        for (var i = 0; i < 5; i++)
        {
            _throttle.RegisterFailure($"user{i}", "203.0.113.1");
        }

        Assert.True(_throttle.IsLockedOut("ktokolwiek", "203.0.113.1"));
        Assert.False(_throttle.IsLockedOut("ktokolwiek", "203.0.113.2"));
    }
}

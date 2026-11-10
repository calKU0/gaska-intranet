using System.ComponentModel.DataAnnotations;

namespace Gaska.Intranet.Web.Security;

public sealed class SessionOptions
{
    public const string SectionName = "Session";

    /// <summary>After this many minutes without activity the user must sign in again.</summary>
    [Range(5, 1440)]
    public int IdleTimeoutMinutes { get; set; } = 60;

    /// <summary>Maximum session length regardless of activity.</summary>
    [Range(1, 168)]
    public int AbsoluteTimeoutHours { get; set; } = 12;

    public TimeSpan IdleTimeout => TimeSpan.FromMinutes(IdleTimeoutMinutes);

    public TimeSpan AbsoluteTimeout => TimeSpan.FromHours(AbsoluteTimeoutHours);
}

public sealed class WebSecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>Company networks in CIDR notation, e.g. "192.168.0.0/16".</summary>
    public List<string> InternalNetworks { get; set; } = [];

    /// <summary>IP addresses of reverse proxies trusted for the X-Forwarded-For header.</summary>
    public List<string> TrustedProxies { get; set; } = [];

    [Range(1, 1000)]
    public int LoginAttemptsPerMinutePerIp { get; set; } = 10;

    [Range(10, 100_000)]
    public int RequestsPerMinutePerIp { get; set; } = 600;
}

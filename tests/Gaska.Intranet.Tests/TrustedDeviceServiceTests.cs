using Gaska.Intranet.Core.Auditing;
using Gaska.Intranet.Core.Organization;
using Gaska.Intranet.Core.Security;
using Gaska.Intranet.Infrastructure;
using Gaska.Intranet.Infrastructure.Security;
using Microsoft.Extensions.Time.Testing;
using Options = Microsoft.Extensions.Options.Options;

namespace Gaska.Intranet.Tests;

public sealed class TrustedDeviceServiceTests : IDisposable
{
    private static readonly ClientInfo Office = new("203.0.113.10", "Mozilla/5.0 Chrome/140");
    private static readonly ClientInfo Home = new("198.51.100.20", "Mozilla/5.0 Chrome/140");

    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero));
    private readonly TrustedDeviceService _service;
    private readonly DirectoryUser _user = new() { AccountName = "jkowalski", Sid = "S-1-5-21-1" };

    public TrustedDeviceServiceTests() =>
        _service = new TrustedDeviceService(_db, Options.Create(new TwoFactorOptions { TrustedDeviceDays = 5 }), new NullAudit(), _clock);

    [Fact]
    public async Task Trusted_device_skips_code_on_same_ip()
    {
        var token = await _service.TrustAsync(_user, null, Office);

        Assert.True(await _service.IsTrustedAsync(_user, token, Office));
    }

    [Fact]
    public async Task New_ip_requires_code_again()
    {
        var token = await _service.TrustAsync(_user, null, Office);

        Assert.False(await _service.IsTrustedAsync(_user, token, Home));
    }

    [Fact]
    public async Task Confirming_on_new_ip_moves_trust_and_rotates_token()
    {
        var first = await _service.TrustAsync(_user, null, Office);
        var second = await _service.TrustAsync(_user, first, Home);

        Assert.NotEqual(first, second);
        Assert.Equal(first.Split('.')[0], second.Split('.')[0]);
        Assert.False(await _service.IsTrustedAsync(_user, first, Home));
        Assert.True(await _service.IsTrustedAsync(_user, second, Home));
    }

    [Fact]
    public async Task Ipv6_address_change_within_same_64_network_keeps_trust()
    {
        var token = await _service.TrustAsync(_user, null, new ClientInfo("2001:db8:abcd:12::1", null));

        Assert.True(await _service.IsTrustedAsync(_user, token, new ClientInfo("2001:db8:abcd:12:aaaa:bbbb:cccc:dddd", null)));
        Assert.False(await _service.IsTrustedAsync(_user, token, new ClientInfo("2001:db8:abcd:99::1", null)));
    }

    [Fact]
    public async Task Trust_expires_after_configured_days()
    {
        var token = await _service.TrustAsync(_user, null, Office);
        _clock.Advance(TimeSpan.FromDays(5) + TimeSpan.FromMinutes(1));

        Assert.False(await _service.IsTrustedAsync(_user, token, Office));
    }

    [Fact]
    public async Task Password_change_invalidates_trust()
    {
        var token = await _service.TrustAsync(_user, null, Office);
        var afterPasswordChange = _user with { PasswordLastSetUtc = _clock.GetUtcNow().UtcDateTime.AddHours(1) };

        Assert.False(await _service.IsTrustedAsync(afterPasswordChange, token, Office));
    }

    [Fact]
    public async Task Token_of_other_user_is_rejected()
    {
        var token = await _service.TrustAsync(_user, null, Office);

        Assert.False(await _service.IsTrustedAsync(_user with { AccountName = "anowak" }, token, Office));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("smieci")]
    [InlineData("00000000000000000000000000000000.zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public async Task Invalid_tokens_are_rejected(string? token) =>
        Assert.False(await _service.IsTrustedAsync(_user, token, Office));

    [Fact]
    public async Task Revoked_and_forgotten_devices_are_not_trusted()
    {
        var revoked = await _service.TrustAsync(_user, null, Office);
        var forgotten = await _service.TrustAsync(_user, null, Home);

        await _service.ForgetAsync(forgotten);
        await _service.RevokeAllAsync(_user.AccountName, "admin");

        Assert.False(await _service.IsTrustedAsync(_user, revoked, Office));
        Assert.False(await _service.IsTrustedAsync(_user, forgotten, Home));
        Assert.Empty(await _service.GetActiveAsync());
    }

    public void Dispose() => _db.Dispose();

    private sealed class NullAudit : IAuditLog
    {
        public Task WriteAsync(string eventType, string? accountName, string? details = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AuditEntry>> GetRecentAsync(int take = 500, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditEntry>>([]);
    }
}

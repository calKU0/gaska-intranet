using Gaska.Intranet.Core.Auditing;
using Gaska.Intranet.Core.Organization;
using Gaska.Intranet.Core.Security;
using Gaska.Intranet.Infrastructure;
using Gaska.Intranet.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Options = Microsoft.Extensions.Options.Options;

namespace Gaska.Intranet.Tests;

public sealed class AuthenticatorServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero));
    private readonly TrustedDeviceService _devices;
    private readonly AuthenticatorService _service;
    private readonly DirectoryUser _user = new() { AccountName = "jkowalski", Sid = "S-1-5-21-1" };

    public AuthenticatorServiceTests()
    {
        var options = Options.Create(new TwoFactorOptions());
        _devices = new TrustedDeviceService(_db, options, new NullAudit(), _clock);
        _service = Create(new EphemeralDataProtectionProvider());
    }

    private AuthenticatorService Create(IDataProtectionProvider keys) =>
        new(_db, keys, Options.Create(new TwoFactorOptions()), _devices, new NullAudit(), _clock, NullLogger<AuthenticatorService>.Instance);

    private string CurrentCode(string key, int stepOffset = 0) =>
        Totp.ComputeCode(Base32.Decode(key), Totp.GetTimeStep(_clock.GetUtcNow()) + stepOffset);

    [Fact]
    public async Task Enrollment_requires_valid_code_and_stores_encrypted_secret()
    {
        var setup = _service.CreateSetup(_user);

        Assert.False(await _service.EnrollAsync(_user, setup.SecretKey, "000000" == CurrentCode(setup.SecretKey) ? "111111" : "000000"));
        Assert.False(await _service.IsEnrolledAsync(_user));

        Assert.True(await _service.EnrollAsync(_user, setup.SecretKey, CurrentCode(setup.SecretKey)));
        Assert.True(await _service.IsEnrolledAsync(_user));

        var stored = Assert.Single(await _service.GetAllAsync());
        Assert.DoesNotContain(setup.SecretKey, stored.ProtectedSecret);
        Assert.StartsWith("otpauth://totp/Gaska%20Intranet%3Ajkowalski?secret=" + setup.SecretKey, setup.OtpAuthUri);
    }

    [Fact]
    public async Task Each_code_works_only_once()
    {
        var setup = _service.CreateSetup(_user);
        await _service.EnrollAsync(_user, setup.SecretKey, CurrentCode(setup.SecretKey));

        _clock.Advance(TimeSpan.FromSeconds(30));
        var code = CurrentCode(setup.SecretKey);

        Assert.True(await _service.VerifyAsync(_user, code));
        Assert.False(await _service.VerifyAsync(_user, code));

        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(await _service.VerifyAsync(_user, CurrentCode(setup.SecretKey)));
    }

    [Fact]
    public async Task Secret_encrypted_by_another_server_gives_a_clear_error_instead_of_a_crash()
    {
        var setup = _service.CreateSetup(_user);
        await _service.EnrollAsync(_user, setup.SecretKey, CurrentCode(setup.SecretKey));

        // Same database, different Data Protection keys (e.g. a developer machine reading the production server's data).
        var otherServer = Create(new EphemeralDataProtectionProvider());
        _clock.Advance(TimeSpan.FromSeconds(30));

        await Assert.ThrowsAsync<AuthenticatorUnreadableException>(() => otherServer.VerifyAsync(_user, CurrentCode(setup.SecretKey)));
    }

    [Fact]
    public async Task Code_used_during_enrollment_cannot_be_replayed_for_login()
    {
        var setup = _service.CreateSetup(_user);
        var code = CurrentCode(setup.SecretKey);
        await _service.EnrollAsync(_user, setup.SecretKey, code);

        Assert.False(await _service.VerifyAsync(_user, code));
    }

    [Fact]
    public async Task Reset_removes_app_and_revokes_trusted_devices()
    {
        var setup = _service.CreateSetup(_user);
        await _service.EnrollAsync(_user, setup.SecretKey, CurrentCode(setup.SecretKey));
        var device = await _devices.TrustAsync(_user, null, new ClientInfo("203.0.113.1", null));

        await _service.ResetAsync(_user.Sid, "admin");

        Assert.False(await _service.IsEnrolledAsync(_user));
        Assert.False(await _devices.IsTrustedAsync(_user, device, new ClientInfo("203.0.113.1", null)));
    }

    [Fact]
    public async Task Verify_fails_for_user_without_app() =>
        Assert.False(await _service.VerifyAsync(_user, "123456"));

    public void Dispose() => _db.Dispose();

    private sealed class NullAudit : IAuditLog
    {
        public Task WriteAsync(string eventType, string? accountName, string? details = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AuditEntry>> GetRecentAsync(int take = 500, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditEntry>>([]);
    }
}

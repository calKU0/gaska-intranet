using Gaska.Intranet.Core.Organization;
using Gaska.Intranet.Core.Security;
using Gaska.Intranet.Core.Text;
using Gaska.Intranet.Infrastructure;
using Gaska.Intranet.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Options = Microsoft.Extensions.Options.Options;

namespace Gaska.Intranet.Tests;

public sealed class LoginCodeServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero));
    private readonly CapturingSender _sms = new();
    private readonly LoginCodeService _service;

    private static readonly DirectoryUser User = new()
    {
        AccountName = "jkowalski",
        Sid = "S-1-5-21-1",
        MobilePhone = "+48 601 222 333",
    };

    public LoginCodeServiceTests() =>
        _service = new LoginCodeService(_db, [_sms], Options.Create(new TwoFactorOptions()), _clock, NullLogger<LoginCodeService>.Instance);

    [Fact]
    public async Task Correct_code_succeeds_only_once()
    {
        var issued = await _service.IssueAsync(User, DeliveryChannel.Sms);

        Assert.True(issued.Succeeded);
        Assert.Equal("601222333", _sms.LastDestination);
        Assert.Equal(VerifyCodeStatus.Success, await _service.VerifyAsync(issued.ChallengeId!.Value, User.AccountName, _sms.LastCode!));
        Assert.Equal(VerifyCodeStatus.NotFound, await _service.VerifyAsync(issued.ChallengeId!.Value, User.AccountName, _sms.LastCode!));
    }

    [Fact]
    public async Task Code_bound_to_account()
    {
        var issued = await _service.IssueAsync(User, DeliveryChannel.Sms);

        Assert.Equal(VerifyCodeStatus.NotFound, await _service.VerifyAsync(issued.ChallengeId!.Value, "ktos-inny", _sms.LastCode!));
    }

    [Fact]
    public async Task Expired_code_is_rejected()
    {
        var issued = await _service.IssueAsync(User, DeliveryChannel.Sms);
        _clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(VerifyCodeStatus.Expired, await _service.VerifyAsync(issued.ChallengeId!.Value, User.AccountName, _sms.LastCode!));
    }

    [Fact]
    public async Task Too_many_wrong_attempts_block_the_code()
    {
        var issued = await _service.IssueAsync(User, DeliveryChannel.Sms);
        var wrong = _sms.LastCode == "000000" ? "111111" : "000000";

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(VerifyCodeStatus.InvalidCode, await _service.VerifyAsync(issued.ChallengeId!.Value, User.AccountName, wrong));
        }

        Assert.Equal(VerifyCodeStatus.TooManyAttempts, await _service.VerifyAsync(issued.ChallengeId!.Value, User.AccountName, wrong));
        Assert.Equal(VerifyCodeStatus.TooManyAttempts, await _service.VerifyAsync(issued.ChallengeId!.Value, User.AccountName, _sms.LastCode!));
    }

    [Fact]
    public async Task Resend_is_throttled_and_new_code_invalidates_old_one()
    {
        var first = await _service.IssueAsync(User, DeliveryChannel.Sms);
        var firstCode = _sms.LastCode!;

        Assert.Equal(IssueCodeStatus.TooManyRequests, (await _service.IssueAsync(User, DeliveryChannel.Sms)).Status);

        _clock.Advance(TimeSpan.FromSeconds(31));
        var second = await _service.IssueAsync(User, DeliveryChannel.Sms);

        Assert.True(second.Succeeded);
        Assert.Equal(VerifyCodeStatus.Expired, await _service.VerifyAsync(first.ChallengeId!.Value, User.AccountName, firstCode));
    }

    [Fact]
    public async Task Channel_without_destination_is_unavailable()
    {
        var noPhone = User with { MobilePhone = null };

        Assert.Empty(_service.GetDeliveryOptions(noPhone));
        Assert.Equal(IssueCodeStatus.ChannelUnavailable, (await _service.IssueAsync(noPhone, DeliveryChannel.Sms)).Status);
    }

    [Fact]
    public async Task Delivery_failure_is_reported()
    {
        _sms.Fail = true;

        Assert.Equal(IssueCodeStatus.DeliveryFailed, (await _service.IssueAsync(User, DeliveryChannel.Sms)).Status);
    }

    public void Dispose() => _db.Dispose();

    private sealed class CapturingSender : ILoginCodeSender
    {
        public string? LastDestination { get; private set; }
        public string? LastCode { get; private set; }
        public bool Fail { get; set; }

        public DeliveryChannel Channel => DeliveryChannel.Sms;

        public string? GetDestination(DirectoryUser user) => ContactFormatter.NormalizePhoneForSms(user.MobilePhone);

        public string MaskDestination(string destination) => ContactFormatter.MaskPhone(destination);

        public Task SendAsync(string destination, string code, TimeSpan validFor, CancellationToken ct = default)
        {
            if (Fail)
            {
                throw new InvalidOperationException("Gateway unavailable");
            }

            LastDestination = destination;
            LastCode = code;
            return Task.CompletedTask;
        }
    }
}

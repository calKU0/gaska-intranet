using System.Text;
using Gaska.Intranet.Core.Security;

namespace Gaska.Intranet.Tests;

public class TotpTests
{
    // RFC 6238, Appendix B – SHA1 test vectors (8-digit codes there; we use the last 6 digits).
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    [InlineData(1111111111, "050471")]
    [InlineData(1234567890, "005924")]
    [InlineData(2000000000, "279037")]
    public void Matches_rfc_6238_test_vectors(long unixTime, string expected) =>
        Assert.Equal(expected, Totp.ComputeCode(RfcSecret, Totp.GetTimeStep(DateTimeOffset.FromUnixTimeSeconds(unixTime))));

    [Fact]
    public void Accepts_one_step_of_clock_drift_only()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var step = Totp.GetTimeStep(now);

        Assert.True(Totp.TryVerify(RfcSecret, Totp.ComputeCode(RfcSecret, step - 1), now, out _));
        Assert.True(Totp.TryVerify(RfcSecret, Totp.ComputeCode(RfcSecret, step + 1), now, out _));
        Assert.False(Totp.TryVerify(RfcSecret, Totp.ComputeCode(RfcSecret, step - 3), now, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("abcdef")]
    public void Rejects_malformed_codes(string? code) =>
        Assert.False(Totp.TryVerify(RfcSecret, code, DateTimeOffset.UtcNow, out _));

    [Fact]
    public void Base32_round_trip_and_known_value()
    {
        Assert.Equal("JBSWY3DPEE", Base32.Encode(Encoding.ASCII.GetBytes("Hello!")));
        var secret = Totp.GenerateSecret();
        Assert.Equal(secret, Base32.Decode(Base32.FormatForDisplay(Base32.Encode(secret)).ToLowerInvariant()));
    }

    [Fact]
    public void Otpauth_uri_contains_issuer_account_and_secret()
    {
        var uri = Totp.BuildOtpAuthUri("Gaska Intranet", "jkowalski", "JBSWY3DPEHPK3PXP");

        Assert.Equal(
            "otpauth://totp/Gaska%20Intranet%3Ajkowalski?secret=JBSWY3DPEHPK3PXP&issuer=Gaska%20Intranet&algorithm=SHA1&digits=6&period=30",
            uri);
    }

    [Fact]
    public void Otpauth_uri_appends_optional_logo()
    {
        var uri = Totp.BuildOtpAuthUri("Gaska Intranet", "jkowalski", "JBSWY3DPEHPK3PXP", "https://intranet.example.com/img/mark.png");

        Assert.EndsWith("&image=https%3A%2F%2Fintranet.example.com%2Fimg%2Fmark.png", uri);
    }
}

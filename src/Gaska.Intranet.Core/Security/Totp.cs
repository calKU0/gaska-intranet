using System.Security.Cryptography;
using System.Text;

namespace Gaska.Intranet.Core.Security;

/// <summary>
/// Time-based one-time passwords (RFC 6238) with the parameters every authenticator app supports:
/// HMAC-SHA1, 6 digits, 30-second period. Compatible with Google Authenticator, Microsoft Authenticator,
/// Authy, 1Password, Bitwarden, etc.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int PeriodSeconds = 30;

    /// <summary>160-bit secret – the length recommended by RFC 4226.</summary>
    public static byte[] GenerateSecret() => RandomNumberGenerator.GetBytes(20);

    public static long GetTimeStep(DateTimeOffset time) => time.ToUnixTimeSeconds() / PeriodSeconds;

    public static string ComputeCode(byte[] secret, long timeStep)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, timeStep);

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);

        // Dynamic truncation (RFC 4226, section 5.3).
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    /// <summary>
    /// Checks the code against the current time step and <paramref name="allowedDrift"/> steps before/after
    /// (tolerates phone clocks that are slightly off). Returns the matched time step, used to block replays.
    /// </summary>
    public static bool TryVerify(byte[] secret, string? code, DateTimeOffset now, out long matchedTimeStep, int allowedDrift = 1)
    {
        matchedTimeStep = 0;
        var normalized = new string((code ?? string.Empty).Where(char.IsDigit).ToArray());
        if (normalized.Length != Digits)
        {
            return false;
        }

        var current = GetTimeStep(now);
        for (var step = current - allowedDrift; step <= current + allowedDrift; step++)
        {
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(ComputeCode(secret, step)), Encoding.ASCII.GetBytes(normalized)))
            {
                matchedTimeStep = step;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// "otpauth://" URI encoded in the QR code. Format: https://github.com/google/google-authenticator/wiki/Key-Uri-Format
    /// </summary>
    /// <param name="imageUrl">
    /// Optional public HTTPS URL of a square PNG logo (non-standard "image" parameter). Shown by apps such as
    /// FreeOTP, 2FAS or Aegis; Google and Microsoft Authenticator ignore it.
    /// </param>
    public static string BuildOtpAuthUri(string issuer, string accountName, string base32Secret, string? imageUrl = null)
    {
        var label = Uri.EscapeDataString($"{issuer}:{accountName}");
        var uri = $"otpauth://totp/{label}?secret={base32Secret}&issuer={Uri.EscapeDataString(issuer)}" +
                  $"&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
        return string.IsNullOrWhiteSpace(imageUrl) ? uri : uri + "&image=" + Uri.EscapeDataString(imageUrl);
    }
}

/// <summary>Base32 (RFC 4648, no padding) – the format authenticator apps expect for secrets.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return sb.ToString();
    }

    public static byte[] Decode(string value)
    {
        var clean = value.Replace(" ", string.Empty).Replace("-", string.Empty).TrimEnd('=').ToUpperInvariant();
        var result = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;

        foreach (var c in clean)
        {
            var index = Alphabet.IndexOf(c);
            if (index < 0)
            {
                throw new FormatException($"Invalid Base32 character '{c}'.");
            }

            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                result.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. result];
    }

    /// <summary>"JBSWY3DPEHPK3PXP" → "JBSW Y3DP EHPK 3PXP" – easier to type by hand.</summary>
    public static string FormatForDisplay(string base32) =>
        string.Join(' ', Enumerable.Range(0, (base32.Length + 3) / 4).Select(i => base32.Substring(i * 4, Math.Min(4, base32.Length - i * 4))));
}

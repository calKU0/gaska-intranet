namespace Gaska.Intranet.Core.Text;

/// <summary>Formatting and masking of contact data.</summary>
public static class ContactFormatter
{
    /// <summary>
    /// Normalizes a phone number to digits in the format expected by the SMS gateway
    /// (9 digits for Polish numbers, e.g. "+48 600-123-456" → "600123456").
    /// Foreign numbers keep the country prefix (without "+").
    /// </summary>
    public static string? NormalizePhoneForSms(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00", StringComparison.Ordinal))
        {
            digits = digits[2..];
        }

        if (digits.Length == 11 && digits.StartsWith("48", StringComparison.Ordinal))
        {
            digits = digits[2..];
        }

        return digits.Length >= 9 ? digits : null;
    }

    /// <summary>"600123456" → "*** *** 456"</summary>
    public static string MaskPhone(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length <= 3 ? "***" : $"*** *** {digits[^3..]}";
    }

    /// <summary>"jan.kowalski@example.com" → "ja*********@example.com"</summary>
    public static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0)
        {
            return "***";
        }

        var local = email[..at];
        var visible = local.Length <= 2 ? local[..1] : local[..2];
        return visible + new string('*', Math.Max(3, local.Length - visible.Length)) + email[at..];
    }

    /// <summary>Mobile number formatted for display: "600 123 456".</summary>
    public static string? FormatPhoneForDisplay(string? phone)
    {
        var normalized = NormalizePhoneForSms(phone);
        if (normalized is null)
        {
            return string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        }

        return normalized.Length == 9
            ? $"{normalized[..3]} {normalized[3..6]} {normalized[6..]}"
            : "+" + normalized;
    }

    /// <summary>"tel:" link for the browser / phone.</summary>
    public static string? ToTelUri(string? phone)
    {
        var normalized = NormalizePhoneForSms(phone);
        if (normalized is null)
        {
            var digits = phone is null ? string.Empty : new string(phone.Where(char.IsDigit).ToArray());
            return digits.Length == 0 ? null : $"tel:{digits}";
        }

        return normalized.Length == 9 ? $"tel:+48{normalized}" : $"tel:+{normalized}";
    }
}

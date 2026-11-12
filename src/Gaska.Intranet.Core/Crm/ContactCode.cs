using System.Text;
using System.Text.RegularExpressions;

namespace Gaska.Intranet.Core.Crm;

/// <summary>Contact data stored in a business card's QR code – a vCard (BEGIN:VCARD) or a MeCard (MECARD:…).</summary>
public static partial class ContactCode
{
    /// <summary>Fields from the QR text; null when it holds no contact data (e.g. only a link or free text).</summary>
    public static BusinessCardFields? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        if (text.StartsWith("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase))
        {
            return ParseVCard(text);
        }

        if (text.StartsWith("MECARD:", StringComparison.OrdinalIgnoreCase))
        {
            return ParseMeCard(text[7..]);
        }

        // A bare website link: not a contact, but the address is exact.
        if (WebLinkRegex().IsMatch(text))
        {
            return new BusinessCardFields { Website = text };
        }

        return null;
    }

    private static BusinessCardFields ParseVCard(string text)
    {
        // Folded lines (RFC 6350: a line starting with a space continues the previous one).
        text = Regex.Replace(text.Replace("\r\n", "\n").Replace('\r', '\n'), @"\n[ \t]", string.Empty);

        string? fullName = null, first = null, last = null, company = null, title = null, website = null, mobile = null;
        string? street = null, postalCode = null, city = null, country = null;
        var phones = new List<string>();
        var emails = new List<string>();

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var head = line[..colon].Split(';');
            var name = head[0];
            // "item1.TEL" (Apple) → "TEL".
            name = name[(name.LastIndexOf('.') + 1)..].ToUpperInvariant();
            var parameters = string.Join(';', head.Skip(1)).ToUpperInvariant();
            var value = Decode(line[(colon + 1)..], parameters);

            switch (name)
            {
                case "FN":
                    fullName = Unescape(value);
                    break;
                case "N":
                    var parts = SplitEscaped(value, ';');
                    last = parts.ElementAtOrDefault(0);
                    first = parts.ElementAtOrDefault(1);
                    break;
                case "ORG":
                    company = string.Join(' ', SplitEscaped(value, ';').Where(p => p.Length > 0));
                    break;
                case "TITLE":
                case "ROLE":
                    title ??= Unescape(value);
                    break;
                case "TEL":
                    if (parameters.Contains("FAX"))
                    {
                        break;
                    }

                    var phone = value.Replace("tel:", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
                    if (parameters.Contains("CELL") && mobile is null)
                    {
                        mobile = phone;
                    }
                    else
                    {
                        phones.Add(phone);
                    }

                    break;
                case "EMAIL":
                    emails.Add(value.Trim());
                    break;
                case "URL":
                    website ??= Unescape(value);
                    break;
                case "ADR":
                    // PO box; extended; street; locality; region; postal code; country
                    var adr = SplitEscaped(value, ';');
                    if (street is null || parameters.Contains("WORK"))
                    {
                        street = Join(adr.ElementAtOrDefault(2), adr.ElementAtOrDefault(1));
                        city = adr.ElementAtOrDefault(3);
                        postalCode = adr.ElementAtOrDefault(5);
                        country = adr.ElementAtOrDefault(6);
                    }

                    break;
            }
        }

        return new BusinessCardFields
        {
            FullName = fullName,
            FirstName = first,
            LastName = last,
            Company = company,
            Position = title,
            Mobile = mobile,
            Phones = phones,
            Emails = emails,
            Website = website,
            Street = street,
            PostalCode = postalCode,
            City = city,
            Country = country,
        };
    }

    private static BusinessCardFields ParseMeCard(string text)
    {
        string? first = null, last = null, company = null, website = null, street = null, postalCode = null, city = null, country = null;
        var phones = new List<string>();
        var emails = new List<string>();

        foreach (var field in SplitEscaped(text, ';'))
        {
            var colon = field.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var value = field[(colon + 1)..].Trim();
            switch (field[..colon].ToUpperInvariant())
            {
                case "N":
                    // "Kowalski,Jan"
                    var parts = value.Split(',', 2);
                    last = parts[0];
                    first = parts.ElementAtOrDefault(1);
                    break;
                case "ORG":
                    company = value;
                    break;
                case "TEL":
                    phones.Add(value);
                    break;
                case "EMAIL":
                    emails.Add(value);
                    break;
                case "URL":
                    website ??= value;
                    break;
                case "ADR":
                    // Same order as vCard, comma-separated.
                    var adr = value.Split(',');
                    street = Join(adr.ElementAtOrDefault(2), adr.ElementAtOrDefault(1));
                    city = adr.ElementAtOrDefault(3);
                    postalCode = adr.ElementAtOrDefault(5);
                    country = adr.ElementAtOrDefault(6);
                    break;
            }
        }

        return new BusinessCardFields
        {
            FirstName = first,
            LastName = last,
            Company = company,
            Phones = phones,
            Emails = emails,
            Website = website,
            Street = street,
            PostalCode = postalCode,
            City = city,
            Country = country,
        };
    }

    /// <summary>vCard 2.1 values may be quoted-printable (ENCODING=QUOTED-PRINTABLE), in the given CHARSET.</summary>
    private static string Decode(string value, string parameters)
    {
        if (!parameters.Contains("QUOTED-PRINTABLE"))
        {
            return value;
        }

        var bytes = new List<byte>();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '=' && i + 2 < value.Length && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            {
                bytes.Add(Convert.ToByte(value.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>Splits on a separator not preceded by a backslash and unescapes the parts.</summary>
    private static List<string> SplitEscaped(string value, char separator)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                current.Append(value[i]).Append(value[i + 1]);
                i++;
            }
            else if (value[i] == separator)
            {
                parts.Add(Unescape(current.ToString()));
                current.Clear();
            }
            else
            {
                current.Append(value[i]);
            }
        }

        parts.Add(Unescape(current.ToString()));
        return parts;
    }

    private static string Unescape(string value) =>
        value.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\:", ":").Replace("\\\\", "\\").Trim();

    private static string? Join(string? a, string? b) =>
        string.Join(", ", new[] { a, b }.Where(p => !string.IsNullOrWhiteSpace(p))) is { Length: > 0 } s ? s : null;

    [GeneratedRegex(@"^(https?://|www\.)\S+$", RegexOptions.IgnoreCase)]
    private static partial Regex WebLinkRegex();
}

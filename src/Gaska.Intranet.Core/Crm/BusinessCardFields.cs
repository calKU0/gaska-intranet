using System.Globalization;
using System.Text;

namespace Gaska.Intranet.Core.Crm;

/// <summary>
/// Business-card fields as delivered by a structured source (QR code with a vCard/MeCard, language model) – already
/// split into fields, but not cleaned up yet. <see cref="BusinessCardParser.FromFields"/> turns them into
/// <see cref="BusinessCardData"/> the same way for every source.
/// </summary>
public sealed record BusinessCardFields
{
    public string? Company { get; init; }
    public string? FullName { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Position { get; init; }
    public string? Mobile { get; init; }
    public IReadOnlyList<string> Phones { get; init; } = [];
    public IReadOnlyList<string> Emails { get; init; } = [];
    public string? Website { get; init; }
    public string? Street { get; init; }
    public string? PostalCode { get; init; }
    public string? City { get; init; }

    /// <summary>Country as printed or already in Polish ("France" / "Francja").</summary>
    public string? Country { get; init; }

    public string? Nip { get; init; }
}

public static partial class BusinessCardParser
{
    private static readonly HashSet<string> PolandNames = new(StringComparer.OrdinalIgnoreCase) { "poland", "polska", "pl" };

    /// <summary>Cleans structured fields: name/position case, phone format, e-mail and website, NIP, Polish country name.</summary>
    public static BusinessCardData FromFields(BusinessCardFields fields, string rawText)
    {
        var emails = fields.Emails
            .Select(CleanEmail)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var www = CleanWebsite(fields.Website)
            ?? emails.Select(e => e[(e.IndexOf('@') + 1)..]).Where(d => !IsPublicMailDomain(d)).Select(d => "www." + d).FirstOrDefault();

        var phones = new[] { fields.Mobile }
            .Concat(fields.Phones)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .SelectMany(p => FindPhones([p!], out _))
            .Distinct()
            .ToList();

        var name = Blank(fields.FullName)
            ?? Blank(string.Join(' ', new[] { fields.FirstName, fields.LastName }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim())));

        var country = Blank(fields.Country);
        if (country is not null)
        {
            country = PolandNames.Contains(country) ? null : FindCountry(country) ?? NiceCase(country);
        }

        return new BusinessCardData
        {
            RawText = rawText,
            CompanyName = Blank(fields.Company),
            PersonName = name is null ? null : NiceCase(name),
            Position = Blank(fields.Position) is { } position ? NiceCase(position) : null,
            Nip = country is null ? Nip.Normalize(fields.Nip) : null,
            Emails = emails,
            Phones = phones,
            Www = www,
            Address = Blank(fields.Street),
            PostalCode = Blank(fields.PostalCode),
            City = Blank(fields.City) is { } city ? NiceCase(city) : null,
            Country = country,
        };
    }

    /// <summary>
    /// Combines two readings of the same card: the fields of <paramref name="preferred"/> (a QR code – exact) win, the
    /// gaps are filled from <paramref name="other"/>; phones and e-mails from both, the preferred ones first.
    /// QR codes often carry plain ASCII ("EXEMPLE", "allee") – when the photo reads the same text with its diacritics
    /// and case ("Exemplé", "allée"), the photo's spelling is kept.
    /// </summary>
    public static BusinessCardData Merge(BusinessCardData preferred, BusinessCardData other)
    {
        // The address goes as one block – a street from one source with a city from the other would mix two addresses.
        var preferredAddress = preferred.Address is not null || preferred.City is not null;
        var sameAddress = preferredAddress && (preferred.PostalCode is null || other.PostalCode is null
            || Digits(preferred.PostalCode) == Digits(other.PostalCode));
        var address = preferredAddress ? preferred : other;
        return new BusinessCardData
        {
            RawText = string.Join("\n\n", new[] { preferred.RawText, other.RawText }.Where(t => !string.IsNullOrWhiteSpace(t))),
            CompanyName = Pick(preferred.CompanyName, other.CompanyName),
            PersonName = Pick(preferred.PersonName, other.PersonName),
            Position = Pick(preferred.Position, other.Position),
            Nip = preferred.Nip ?? other.Nip,
            Emails = preferred.Emails.Concat(other.Emails).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Phones = MergePhones(preferred.Phones, other.Phones),
            Www = preferred.Www ?? other.Www,
            Address = sameAddress ? Pick(preferred.Address, other.Address) : address.Address,
            PostalCode = address.PostalCode,
            City = sameAddress ? Pick(preferred.City, other.City) : address.City,
            Country = preferred.Country ?? other.Country,
        };

        static string? Pick(string? exact, string? read) =>
            exact is not null && read is not null && string.Equals(FoldToAscii(exact), FoldToAscii(read), StringComparison.OrdinalIgnoreCase)
                ? read
                : exact ?? read;
    }

    /// <summary>"JDupont@Exemplé.FR " → "jdupont@exemple.fr"; null when it is not an e-mail address.</summary>
    internal static string? CleanEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var email = FoldToAscii(value.Trim().Trim('<', '>', '.', ',', ';').Replace("mailto:", string.Empty, StringComparison.OrdinalIgnoreCase))
            .ToLowerInvariant();
        return EmailRegex().Match(email) is { Success: true } m && m.Value == email ? email : null;
    }

    /// <summary>"https://www.Exemplé.fr/" → "www.exemple.fr".</summary>
    internal static string? CleanWebsite(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var site = FoldToAscii(value.Trim()).ToLowerInvariant();
        site = site.Replace("https://", string.Empty).Replace("http://", string.Empty).TrimEnd('/');
        if (site.Length < 4 || !site.Contains('.') || site.Contains(' ') || site.Contains('@'))
        {
            return null;
        }

        return site.StartsWith("www.", StringComparison.Ordinal) || site.Count(c => c == '.') > 1 ? site : "www." + site;
    }

    /// <summary>
    /// Phone number as GoNet stores it: digits only, with the leading "+" of an international number
    /// ("+33 (0)6 12-34-56-78" → "+33612345678", "601 222 333" → "601222333").
    /// </summary>
    public static string CompactPhone(string phone)
    {
        var digits = Digits(phone.Replace("(0)", string.Empty));
        return digits.Length > 0 && phone.TrimStart().StartsWith('+') ? "+" + digits : digits;
    }

    /// <summary>
    /// E-mail addresses and website domains are ASCII in practice – a model reading "Exemplé" above the address tends
    /// to carry the accent into it.
    /// </summary>
    private static string FoldToAscii(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c switch { 'ł' => 'l', 'Ł' => 'L', 'ø' => 'o', 'ß' => 's', _ => c });
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static List<string> MergePhones(IEnumerable<string> preferred, IEnumerable<string> other)
    {
        var result = new List<string>();
        foreach (var phone in preferred.Concat(other))
        {
            var digits = Digits(phone.Replace("(0)", string.Empty));
            // "+48 600 100 200" and "600 100 200" are the same number.
            if (!result.Any(p => Digits(p.Replace("(0)", string.Empty)).EndsWith(digits, StringComparison.Ordinal)
                                 || digits.EndsWith(Digits(p.Replace("(0)", string.Empty)), StringComparison.Ordinal)))
            {
                result.Add(phone);
            }
        }

        return result;
    }

    private static string Digits(string value) => new(value.Where(char.IsDigit).ToArray());

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

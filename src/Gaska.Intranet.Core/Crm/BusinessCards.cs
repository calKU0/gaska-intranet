namespace Gaska.Intranet.Core.Crm;

/// <summary>Data recognised on a business card. Every field is a best guess and must be reviewed by the user.</summary>
public sealed record BusinessCardData
{
    public string RawText { get; init; } = string.Empty;
    public string? CompanyName { get; init; }
    public string? PersonName { get; init; }
    public string? Position { get; init; }
    public string? Nip { get; init; }
    public IReadOnlyList<string> Emails { get; init; } = [];
    public IReadOnlyList<string> Phones { get; init; } = [];
    public string? Www { get; init; }
    public string? Address { get; init; }
    public string? PostalCode { get; init; }
    public string? City { get; init; }

    /// <summary>Country printed on a foreign card (Polish name, e.g. "Włochy"); <c>null</c> = Poland / not printed.</summary>
    public string? Country { get; init; }

    public bool IsForeign => Country is not null;
}

/// <summary>Official company data from a public register.</summary>
/// <param name="Source">Where the data comes from, shown to the user (e.g. "GUS", "Biała lista VAT").</param>
public sealed record CompanyInfo(
    string Name,
    string? Nip,
    string? Regon,
    string? Address,
    string? PostalCode,
    string? City,
    string? Voivodeship = null,
    string Source = "");

public interface IBusinessCardReader
{
    /// <summary>Runs OCR on the photo and extracts the fields.</summary>
    Task<BusinessCardData> ReadAsync(Stream image, CancellationToken ct = default);
}

public interface ICompanyRegistry
{
    /// <summary>Company by NIP (null when not found or the register is unavailable).</summary>
    Task<CompanyInfo?> FindByNipAsync(string nip, CancellationToken ct = default);

    /// <summary>Company by REGON (9 or 14 digits).</summary>
    Task<CompanyInfo?> FindByRegonAsync(string regon, CancellationToken ct = default);
}

/// <summary>Polish statistical number (REGON, 9 or 14 digits).</summary>
public static class Regon
{
    /// <summary>Digits only; null when the length or checksum is wrong.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        int[]? weights = digits.Length switch
        {
            9 => [8, 9, 2, 3, 4, 5, 6, 7],
            14 => [2, 4, 8, 5, 0, 9, 7, 3, 6, 1, 2, 4, 8],
            _ => null,
        };

        if (weights is null)
        {
            return null;
        }

        var sum = weights.Select((w, i) => w * (digits[i] - '0')).Sum();
        return sum % 11 % 10 == digits[^1] - '0' ? digits : null;
    }
}

/// <summary>Polish tax identification number (NIP).</summary>
public static class Nip
{
    private static readonly int[] Weights = [6, 5, 7, 2, 3, 4, 5, 6, 7];

    /// <summary>"PL 123-456-78-90" → "1234567890"; null when it is not a valid NIP (checksum included).</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        return IsValid(digits) ? digits : null;
    }

    public static bool IsValid(string digits)
    {
        if (digits.Length != 10 || !digits.All(char.IsDigit))
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (digits[i] - '0') * Weights[i];
        }

        var check = sum % 11;
        return check != 10 && check == digits[9] - '0';
    }

    /// <summary>"1234567890" → "123-456-78-90" (the format Comarch ERP XL accepts).</summary>
    public static string Format(string digits) =>
        digits.Length == 10 ? $"{digits[..3]}-{digits[3..6]}-{digits[6..8]}-{digits[8..]}" : digits;
}

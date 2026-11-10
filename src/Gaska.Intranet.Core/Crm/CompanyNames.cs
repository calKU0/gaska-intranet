using Gaska.Intranet.Core.Text;

namespace Gaska.Intranet.Core.Crm;

/// <summary>Comparing company names and web domains for duplicate detection.</summary>
public static class CompanyNames
{
    /// <summary>Legal forms and their parts (after punctuation is removed) – not part of the distinctive name.</summary>
    private static readonly HashSet<string> LegalWords = new(StringComparer.Ordinal)
    {
        "sp", "spolka", "oo", "sa", "sj", "sk", "sc", "spj", "spk", "ograniczona", "odpowiedzialnoscia", "jawna",
        "komandytowa", "cywilna", "akcyjna", "pphu", "phu", "fhu", "ppuh", "puh", "sro", "spol", "spa", "srl", "bv", "nv",
        "gmbh", "ltd", "limited", "co", "llc", "inc", "kft", "ag", "sas", "sarl", "oy", "ab", "as", "kg",
    };

    /// <summary>
    /// Distinctive part of a company name: "NOVA GROUP, S.R.O." and "NOVA GROUP s.r.o." → "nova group";
    /// "AGRO-SERWIS Sp. z o.o." → "agro serwis". Single letters (from "s.r.o.", "z o.o.") are dropped too.
    /// </summary>
    public static string Core(string? name)
    {
        var words = TextSearch.Normalize(new string((name ?? string.Empty)
                .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
                .ToArray()))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1 && !LegalWords.Contains(w));
        return string.Join(' ', words);
    }

    /// <summary>
    /// Same company? Equal distinctive names, or one fully contained in the other when it is long enough
    /// to be distinctive ("nova group" in "nova group praha").
    /// </summary>
    public static bool Similar(string? a, string? b)
    {
        var x = Core(a);
        var y = Core(b);
        if (x.Length < 3 || y.Length < 3)
        {
            return false;
        }

        if (x == y)
        {
            return true;
        }

        var (shorter, longer) = x.Length <= y.Length ? (x, y) : (y, x);
        return shorter.Length >= 6 && $" {longer} ".Contains($" {shorter} ", StringComparison.Ordinal);
    }

    /// <summary>"http://www.klcz.cz/kontakt" / "linzhao@klcz.cz" → "klcz.cz"; public mailboxes (gmail.com…) → null.</summary>
    public static string? Domain(string? webOrEmail)
    {
        if (string.IsNullOrWhiteSpace(webOrEmail))
        {
            return null;
        }

        var value = webOrEmail.Trim().ToLowerInvariant();
        value = value.Contains('@') ? value[(value.IndexOf('@') + 1)..] : value;
        value = value.Replace("https://", string.Empty).Replace("http://", string.Empty);
        if (value.StartsWith("www.", StringComparison.Ordinal))
        {
            value = value[4..];
        }

        value = value.Split('/', '?', '#')[0].Trim('.');
        return value.Contains('.') && !IsPublicMailDomain(value) ? value : null;
    }

    public static bool IsPublicMailDomain(string domain) =>
        domain is "gmail.com" or "wp.pl" or "o2.pl" or "onet.pl" or "onet.eu" or "interia.pl" or "interia.eu" or "op.pl"
            or "poczta.fm" or "tlen.pl" or "outlook.com" or "hotmail.com" or "yahoo.com" or "icloud.com" or "gazeta.pl"
            or "vp.pl" or "buziaczek.pl" or "poczta.onet.pl" or "live.com" or "seznam.cz" or "web.de" or "gmx.de";
}

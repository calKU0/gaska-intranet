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
    /// Words and signs joining two names – written in many ways in the same company's name ("Smith &amp; Wesson",
    /// "SMITH AND WESSON", "Smith + Wesson", "Smith i Wesson") – so they are not part of the distinctive name.
    /// </summary>
    private static readonly HashSet<string> Connectors = new(StringComparer.Ordinal) { "and", "und", "et", "plus", "i", "e", "y" };

    /// <summary>Signs written in place of "and".</summary>
    private static readonly char[] ConnectorSigns = ['&', '+'];

    /// <summary>
    /// Distinctive part of a company name: "NOVA GROUP, S.R.O." and "NOVA GROUP s.r.o." → "nova group";
    /// "AGRO-SERWIS Sp. z o.o." → "agro serwis"; "Smith &amp; Wesson" and "SMITH AND WESSON" → "smith wesson".
    /// Single letters (from "s.r.o.", "z o.o.") are dropped too.
    /// </summary>
    public static string Core(string? name)
    {
        var words = TextSearch.Normalize(new string((name ?? string.Empty)
                .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
                .ToArray()))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1 && !LegalWords.Contains(w) && !Connectors.Contains(w));
        return string.Join(' ', words);
    }

    /// <summary>
    /// The name as one run of letters and digits, without legal forms and connectors – for names written with
    /// dots, signs or without spaces: "X.Y.Z.A. SpA" → "xyza", "P&amp;Q Sp. z o.o." = "P AND Q" = "PQ" → "pq",
    /// "Silva &amp; Penas" = "SILVAPENAS" → "silvapenas". Single letters are kept here (they are the acronym).
    /// </summary>
    public static string Compact(string? name)
    {
        var text = new string((name ?? string.Empty).Replace(".", string.Empty)
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
            .ToArray());
        var words = TextSearch.Normalize(text)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !LegalWords.Contains(w) && !Connectors.Contains(w) && w != "z"); // "z" of "sp. z o.o."
        return string.Concat(words);
    }

    /// <summary>
    /// Ways a name part with "&amp;" / "+" / "and" may be written in the CRM, for a text search:
    /// "P&amp;Q" → "P&amp;Q", "P &amp; Q", "P AND Q", "P+Q", "P + Q", "PQ". Without a connector – just the text.
    /// </summary>
    /// <param name="text">Name part, e.g. "P&amp;Q" or "Silva and Penas".</param>
    /// <param name="joinShortParts">
    /// Also "PQ" for short parts – fine when the results are checked afterwards, too broad for a plain list search
    /// ("PW" is inside many names); parts of 3+ letters are always joined too ("SILVAPENAS").
    /// </param>
    public static IReadOnlyList<string> SearchVariants(string text, bool joinShortParts = true)
    {
        var trimmed = text.Trim();
        var parts = System.Text.RegularExpressions.Regex.Split(trimmed, @"\s*[&+]\s*|\s+(?:and|AND|And|und|UND)\s+");
        if (parts.Length < 2 || parts.Any(p => p.Length == 0))
        {
            return [trimmed];
        }

        var joined = joinShortParts || parts.All(p => p.Length >= 3);
        return new[] { "&", " & ", " AND ", "+", " + " }
            .Concat(joined ? [""] : [])
            .Select(separator => string.Join(separator, parts))
            .Prepend(trimmed)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>A lone "&amp;", "+", "and" – says nothing about the company on its own.</summary>
    public static bool IsConnector(string word) =>
        (word.Length == 1 && ConnectorSigns.Contains(word[0])) || Connectors.Contains(TextSearch.Normalize(word));

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

    /// <summary>A legal form or its part ("sp", "sro", "gmbh") – lower case, without dots.</summary>
    public static bool IsLegalWord(string word) => LegalWords.Contains(word);

    public static bool IsPublicMailDomain(string domain) =>
        domain is "gmail.com" or "wp.pl" or "o2.pl" or "onet.pl" or "onet.eu" or "interia.pl" or "interia.eu" or "op.pl"
            or "poczta.fm" or "tlen.pl" or "outlook.com" or "hotmail.com" or "yahoo.com" or "icloud.com" or "gazeta.pl"
            or "vp.pl" or "buziaczek.pl" or "poczta.onet.pl" or "live.com" or "seznam.cz" or "web.de" or "gmx.de";
}

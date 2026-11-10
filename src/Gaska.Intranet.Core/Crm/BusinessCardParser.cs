using System.Globalization;
using System.Text.RegularExpressions;

namespace Gaska.Intranet.Core.Crm;

/// <summary>
/// Heuristic extraction of business-card fields from OCR text. Covers Polish cards (NIP, "00-000 City", sp. z o.o.)
/// and the foreign ones suppliers hand out (international phones, SpA / S.r.l. / BV / s.r.o. / GmbH, foreign postal
/// codes, names printed in capitals).
/// </summary>
public static partial class BusinessCardParser
{
    /// <summary>Polish legal forms and words typical for Polish company names (case-insensitive).</summary>
    private static readonly string[] PolishCompanyWords =
    [
        "sp. z o.o", "sp.z o.o", "spółka z ograniczoną", "s.a.", " sa ", "sp.j", "sp. j", "spółka jawna", "sp.k", "sp. k",
        "spółka komandytowa", "s.c.", "spółka cywilna", "p.p.h.u", "pphu", "p.h.u", "phu", "f.h.u", "fhu", "przedsiębiorstwo",
        "zakład", "gospodarstwo", "firma", "agro", "hurtownia", "serwis",
    ];

    private static readonly string[] PositionWords =
    [
        "prezes", "dyrektor", "kierownik", "właściciel", "wlasciciel", "manager", "menedżer", "specjalista", "przedstawiciel",
        "handlowiec", "doradca", "koordynator", "członek zarządu", "zarząd", "sales", "director", "owner", "ceo", "kupiec",
        "zaopatrzeniowiec", "księgowa", "asystent", "agent", "executive", "account", "buyer", "purchas", "export", "import",
        "marketing", "logistic", "president", "head of", "representative", "consultant", "engineer", "inżynier", "geschäftsführer",
        "verkauf", "vendite", "commerciale", "responsabile",
    ];

    /// <summary>Words that never form a person's name (countries, departments, company words).</summary>
    private static readonly HashSet<string> NotNameWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "republic", "netherlands", "italy", "italia", "poland", "polska", "germany", "czech", "slovakia", "group", "office",
        "warehouse", "park", "airport", "headquarters", "department", "manager", "marketing", "sales", "spare", "parts",
        "specialists", "international", "address", "phone", "mobile", "chain", "company", "the", "export", "import",
        "director", "area", "agent", "service", "services", "aftermarket", "europe", "trading", "holding", "industries",
        "dział", "sprzedaży", "biuro", "oddział", "centrala", "magazyn", "ulica", "street", "road",
    };

    /// <summary>Country names and spellings printed on cards → Polish name as in the GoNet country dictionary.</summary>
    private static readonly (string Pattern, string Name)[] Countries =
    [
        (@"ital(y|ia|ien)", "Włochy"),
        (@"(the\s)?netherlands|nederland|holland", "Holandia"),
        (@"czech(\s?republic|ia)?|česk[áa]\s?republika|cesko|česko", "Republika Czeska"),
        (@"germany|deutschland", "Niemcy"),
        (@"slovakia|slovensko", "Słowacja"),
        (@"ukraine|ukraina", "Ukraina"),
        (@"lithuania|lietuva", "Litwa"),
        (@"austria|österreich", "Austria"),
        (@"france", "Francja"),
        (@"spain|españa|espana", "Hiszpania"),
        (@"hungary|magyarország", "Węgry"),
        (@"denmark|danmark", "Dania"),
        (@"sweden|sverige", "Szwecja"),
        (@"belgium|belgique|belgië", "Belgia"),
        (@"united\skingdom|england", "Wielka Brytania"),
        (@"china|p\.r\.c\.", "Chiny"),
        (@"turkey|türkiye", "Turcja"),
    ];

    private static readonly string[] KnownTlds =
        ["pl", "com", "eu", "net", "org", "de", "it", "cz", "sk", "nl", "info", "biz", "uk", "fr", "es", "at", "hu", "lt", "ua", "ro", "cn", "be", "dk", "se"];

    public static BusinessCardData Parse(string text)
    {
        text = FixOcrConfusions(text);
        var lines = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 1)
            .ToList();

        var country = FindCountry(text);
        var www = FindWebsite(text);
        var emails = FindEmails(text, www);
        www ??= emails.Select(e => e[(e.IndexOf('@') + 1)..]).Where(d => !IsPublicMailDomain(d)).Select(d => "www." + d).FirstOrDefault();

        var phones = FindPhones(lines, out var phoneFreeText);
        var nip = FindNip(text, phoneFreeText, country is null);
        var (address, postalCode, city, addressLine) = FindAddress(lines, country);

        var company = FindCompany(lines, addressLine);
        var position = lines.FirstOrDefault(l =>
            PositionWords.Any(w => l.Contains(w, StringComparison.OrdinalIgnoreCase)) &&
            !IsContactLine(l) && !l.Any(char.IsDigit) && l != company?.Line);
        var person = FindPerson(lines, company?.Line, position);

        return new BusinessCardData
        {
            RawText = text,
            CompanyName = company?.Name,
            PersonName = person,
            Position = position is null ? null : NiceCase(CleanEdges(position)),
            Nip = nip,
            Emails = emails,
            Phones = phones,
            Www = www,
            Address = address,
            PostalCode = postalCode,
            City = city,
            Country = country,
        };
    }

    /// <summary>
    /// Repairs common OCR mistakes: "@" read as "Q"/"©"/"®"/"&amp;", "l" read as "|", spaces inside e-mail addresses,
    /// "o.o." read as "0.0.", "S.r.l." read as "$.r.l.", "C.O.M.A." read as "C.0.M.A.".
    /// </summary>
    internal static string FixOcrConfusions(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.Contains('@') && BrokenEmailRegex().Match(line) is { Success: true } m)
            {
                var local = m.Groups["local"].Value.Replace(" ", string.Empty).Replace('|', 'l');
                line = $"{local}@{m.Groups["domain"].Value}";
            }
            else if (!line.Contains('@') && BrokenEmailTokenRegex().IsMatch(line))
            {
                line = BrokenEmailTokenRegex().Replace(line, "${local}@${domain}");
            }

            if (line.Contains('@'))
            {
                // "marco. rossini@export- -Agency.net" → "marco.rossini@export-Agency.net"
                line = SpacesAroundAtRegex().Replace(line, "@").Replace("|", "l");
                line = SpaceAfterDotRegex().Replace(line, "$1.$2");
                line = BrokenHyphenRegex().Replace(line, "-");
            }

            line = BrokenLegalFormRegex().Replace(line, "z o.o.");
            line = DollarLegalFormRegex().Replace(line, "S.");
            line = ZeroInAcronymRegex().Replace(line, "O");
            lines[i] = line;
        }

        return string.Join('\n', lines);
    }

    private static string? FindCountry(string text)
    {
        foreach (var (pattern, name) in Countries)
        {
            if (Regex.IsMatch(text, $@"(?<!\p{{L}})({pattern})(?!\p{{L}})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return name;
            }
        }

        return null;
    }

    private static string? FindWebsite(string text)
    {
        var www = WwwRegex().Match(text);
        if (!www.Success)
        {
            return null;
        }

        var value = www.Value.Trim('.', ',').ToLowerInvariant();
        value = SchemeRegex().Replace(value, string.Empty);
        return value.TrimEnd('/');
    }

    private static List<string> FindEmails(string text, string? www)
    {
        var siteDomain = www is null ? null : www["www.".Length..].Split('/')[0];

        // "m.verdiatulipchain.eu" – "@" read as "a"; recoverable when the domain is known from the website.
        if (siteDomain is not null)
        {
            text = Regex.Replace(text, $@"(?<![@\w.])(?<local>[A-Za-z0-9._%+\-]{{2,}})a(?<domain>{Regex.Escape(siteDomain)})\b",
                "${local}@${domain}", RegexOptions.IgnoreCase);
        }

        var emails = new List<string>();
        foreach (Match m in EmailRegex().Matches(text))
        {
            var email = m.Value.Trim('.', ',', '-').ToLowerInvariant();
            var at = email.IndexOf('@');
            var domain = FixDomain(email[(at + 1)..], siteDomain);
            email = email[..(at + 1)] + domain;
            if (!emails.Contains(email))
            {
                emails.Add(email);
            }
        }

        return emails;
    }

    /// <summary>"kicz.cz" next to "www.klcz.cz" → "klcz.cz"; "partsservice.ey" → ".eu" (one wrong character).</summary>
    private static string FixDomain(string domain, string? siteDomain)
    {
        if (siteDomain is not null && domain != siteDomain && Distance(domain, siteDomain) <= (siteDomain.Length > 10 ? 2 : 1))
        {
            return siteDomain;
        }

        var dot = domain.LastIndexOf('.');
        var tld = domain[(dot + 1)..];
        if (dot > 0 && !KnownTlds.Contains(tld) && KnownTlds.Where(t => t.Length == tld.Length).FirstOrDefault(t => Distance(t, tld) == 1) is { } fixedTld)
        {
            return domain[..(dot + 1)] + fixedTld;
        }

        return domain;
    }

    /// <summary>
    /// Polish numbers are formatted "601 222 333"; foreign ones keep their country code ("+39 333 1234567").
    /// Numbers labelled as mobile come first (the contact's mobile field takes the first phone).
    /// </summary>
    private static List<string> FindPhones(List<string> lines, out string phoneFreeText)
    {
        var found = new List<(string Phone, bool Mobile)>();
        var rest = new List<string>();

        void Add(string phone, string line)
        {
            if (found.All(p => p.Phone != phone))
            {
                found.Add((phone, MobileLabelRegex().IsMatch(line)));
            }
        }

        foreach (var line in lines)
        {
            var remaining = line;
            foreach (Match m in InternationalPhoneRegex().Matches(line))
            {
                var digits = new string(m.Value.Where(char.IsDigit).ToArray());
                if (digits.StartsWith("48", StringComparison.Ordinal) && digits.Length == 11)
                {
                    Add(FormatPolish(digits[2..]), line);
                }
                else if (digits.Length is >= 9 and <= 15)
                {
                    var phone = WhitespaceRegex().Replace(m.Value.Replace("(0)", " ").Replace('-', ' ').Replace("(", " ").Replace(")", " "), " ").Trim();
                    Add(phone.Replace("+ ", "+"), line);
                }

                remaining = remaining.Replace(m.Value, " ");
            }

            foreach (Match m in PolishPhoneRegex().Matches(remaining))
            {
                var digits = new string(m.Value.Where(char.IsDigit).ToArray());
                if (digits.Length == 9)
                {
                    Add(FormatPolish(digits), line);
                }
            }

            rest.Add(remaining);
        }

        phoneFreeText = string.Join('\n', rest);
        return found.OrderByDescending(p => p.Mobile).Select(p => p.Phone).ToList();
    }

    private static string FormatPolish(string digits) => $"{digits[..3]} {digits[3..6]} {digits[6..]}";

    private static string? FindNip(string text, string phoneFreeText, bool polish)
    {
        // A labelled NIP first ("NIP: 123-456-78-90"), then – on Polish cards – any 10 digits passing the checksum
        // that are not part of a phone number.
        foreach (Match m in LabelledNipRegex().Matches(text))
        {
            if (Nip.Normalize(m.Groups["nip"].Value) is { } nip)
            {
                return nip;
            }
        }

        return polish
            ? NipCandidateRegex().Matches(phoneFreeText).Select(m => Nip.Normalize(m.Value)).FirstOrDefault(n => n is not null)
            : null;
    }

    private static (string? Address, string? PostalCode, string? City, string? Line) FindAddress(List<string> lines, string? country)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            foreach (Match m in PostalCityRegex().Matches(lines[i]))
            {
                var code = m.Groups["code"].Value;
                var polishCode = PolishPostalRegex().IsMatch(code) || (country is null && Regex.IsMatch(code, @"^\d{5}$"));
                if (!polishCode && country is null)
                {
                    continue; // e.g. "1812 RS" on a card without a country – too uncertain
                }

                var city = CleanCity(m.Groups["city"].Value);
                if (city is null)
                {
                    continue;
                }

                if (polishCode)
                {
                    var digits = new string(code.Where(char.IsDigit).ToArray());
                    code = $"{digits[..2]}-{digits[2..]}";
                }

                // Street is either on the same line ("ul. Polna 1, 00-000 City") or on the line above.
                var street = CleanStreet(lines[i][..m.Index]) ?? (i > 0 ? CleanStreet(lines[i - 1]) : null);
                return (street, code, city, lines[i]);
            }
        }

        // No postal code: "Bologna ITALY" – the city is the text before the country name.
        if (country is not null)
        {
            foreach (var line in lines.Where(l => !l.Any(char.IsDigit)))
            {
                var name = Countries.First(c => c.Name == country).Pattern;
                var match = Regex.Match(line, $@"^(?<city>\p{{L}}[\p{{L}}\-\s]{{1,30}}?)[\s,\-]+({name})\b", RegexOptions.IgnoreCase);
                if (match.Success && CleanCity(match.Groups["city"].Value) is { } city)
                {
                    return (null, null, city, line);
                }
            }
        }

        var streetOnly = lines.Where(l => !IsContactLine(l)).Select(CleanStreet).FirstOrDefault(s => s is not null && StreetPrefixRegex().IsMatch(s));
        return (streetOnly, null, null, null);
    }

    /// <summary>"SETTIMO MILANESE (Mi) - Italy" → "Settimo Milanese"; "Haarlem Mobile: …" → "Haarlem".</summary>
    private static string? CleanCity(string value)
    {
        var words = new List<string>();
        foreach (var word in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var bare = word.Trim(',', '.', ':', '-');
            if (bare.Length == 0 || word.EndsWith(':') || LabelWordRegex().IsMatch(bare) || FindCountry(bare) is not null ||
                !bare.All(c => char.IsLetter(c) || c == '-' || c == '\''))
            {
                break;
            }

            words.Add(bare);
            if (word.EndsWith(','))
            {
                break;
            }
        }

        return words.Count == 0 || words.Count > 3 ? null : NiceCase(string.Join(' ', words));
    }

    /// <summary>"Address Via Roma, 15" → "Via Roma, 15"; "Tulpstraat 1 Phone: …" → "Tulpstraat 1".</summary>
    private static string? CleanStreet(string line)
    {
        line = AddressLabelRegex().Replace(line, string.Empty);
        var label = LabelWordRegex().Match(line);
        if (label.Success)
        {
            line = line[..label.Index];
        }

        line = line.Trim(' ', ',', ':', '-');
        var street = StreetWithNumberRegex().Match(line);
        return street.Success ? street.Groups["street"].Value.Trim(' ', ',') : null;
    }

    private static (string Name, string Line)? FindCompany(List<string> lines, string? addressLine)
    {
        foreach (var line in lines.Where(l => !IsContactLine(l)))
        {
            // Cut at the legal form – a second column may follow ("Ve.Ra Group S.r.l. LUCA FERRARI").
            var form = LegalFormRegex().Match(line);
            if (form.Success)
            {
                return (CleanCompany(line[..(form.Index + form.Length)]), line);
            }
        }

        var polish = lines.FirstOrDefault(l => !IsContactLine(l) &&
            PolishCompanyWords.Any(f => $" {l.ToLowerInvariant()} ".Contains(f)));
        if (polish is not null)
        {
            return (CleanCompany(polish), polish);
        }

        // No legal form: the line just above the address ("XYZ Aftermarket" above "Bologna ITALY").
        var index = addressLine is null ? -1 : lines.IndexOf(addressLine);
        if (index > 0 && !IsContactLine(lines[index - 1]) && !lines[index - 1].Any(char.IsDigit) &&
            CleanStreet(lines[index - 1]) is null)
        {
            return (CleanCompany(lines[index - 1]), lines[index - 1]);
        }

        return null;
    }

    private static string? FindPerson(List<string> lines, string? companyLine, string? positionLine)
    {
        var candidates = lines
            .Where(l => l != companyLine && l != positionLine && !IsContactLine(l) && !l.Any(char.IsDigit))
            .Select(CleanEdges)
            .Where(l => PersonRegex().IsMatch(l) && !l.Split(' ').Any(w => NotNameWords.Contains(w)) && FindCountry(l) is null)
            .ToList();

        // "Jan Kowalski" / "Marco ROSSINI" before an all-caps line that may be a slogan.
        var person = candidates.FirstOrDefault(l => l.Any(char.IsLower)) ?? candidates.FirstOrDefault();
        if (person is null)
        {
            // A name in a second column on the company line: "Ve.Ra Group S.r.l. LUCA FERRARI".
            person = lines
                .Select(l => TrailingCapsNameRegex().Match(l))
                .Where(m => m.Success && !m.Groups["name"].Value.Split(' ').Any(w => NotNameWords.Contains(w)))
                .Select(m => m.Groups["name"].Value)
                .FirstOrDefault();
        }

        return person is null ? null : NiceCase(person);
    }

    /// <summary>ALL-CAPS words → "Title Case" (names, cities, positions); mixed case is left alone.</summary>
    private static string NiceCase(string value) =>
        string.Join(' ', value.Split(' ').Select(w =>
            w.Length > 1 && w.All(c => !char.IsLetter(c) || char.IsUpper(c))
                ? CultureInfo.GetCultureInfo("pl-PL").TextInfo.ToTitleCase(w.ToLowerInvariant())
                : w));

    /// <summary>Strips OCR noise at line edges: "- AREA MANAGER __", "Mario Verdi =", "Marco ROSSINI |".</summary>
    private static string CleanEdges(string value) => value.Trim(' ', '-', '_', '=', '|', '~', '—', '.', ',', ':', '*').Trim();

    /// <summary>Like <see cref="CleanEdges"/> but keeps the final dot of "Sp. z o.o." / "S.r.l.".</summary>
    private static string CleanCompany(string value) => value.Trim(' ', '-', '_', '=', '|', '~', '—', ',', ':', '*').Trim();

    private static bool IsContactLine(string line) =>
        line.Contains('@') || line.Contains("www", StringComparison.OrdinalIgnoreCase) ||
        line.Count(char.IsDigit) >= 6 || line.StartsWith("tel", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("nip", StringComparison.OrdinalIgnoreCase);

    private static bool IsPublicMailDomain(string domain) =>
        domain is "gmail.com" or "wp.pl" or "o2.pl" or "onet.pl" or "onet.eu" or "interia.pl" or "op.pl" or "poczta.fm"
            or "tlen.pl" or "outlook.com" or "hotmail.com" or "yahoo.com" or "icloud.com" or "gazeta.pl" or "vp.pl" or "buziaczek.pl";

    private static int Distance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        var current = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();

    /// <summary>"|. wisniewskiQagro-serwis.pl" – a whole line that is an e-mail with "@" misread.</summary>
    [GeneratedRegex(@"^(?<local>[A-Za-z0-9|][A-Za-z0-9._%+\-| ]{0,40}?)\s?[Q©®&]\s?(?<domain>[a-z0-9\-]+(\.[a-z0-9\-]+)*\.(pl|com|eu|net|org|de|info|biz|com\.pl|it|cz|nl|sk))$")]
    private static partial Regex BrokenEmailRegex();

    /// <summary>"linzhaoQklcz.cz" / "export © vera-parts.com" inside a longer line.</summary>
    [GeneratedRegex(@"(?<local>[A-Za-z0-9._%+\-]{2,})(?:\s?[©®]\s?|Q)(?<domain>[a-z0-9\-]+(?:\.[a-z0-9\-]+)*\.[a-z]{2,6})\b")]
    private static partial Regex BrokenEmailTokenRegex();

    [GeneratedRegex(@"\s*@\s*")]
    private static partial Regex SpacesAroundAtRegex();

    [GeneratedRegex(@"(\w)\.\s+(\w)")]
    private static partial Regex SpaceAfterDotRegex();

    [GeneratedRegex(@"\s*-\s*-?\s*(?=[A-Za-z0-9])")]
    private static partial Regex BrokenHyphenRegex();

    [GeneratedRegex(@"\bz\s*[o0]\s*\.\s*[o0]\s*\.?(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BrokenLegalFormRegex();

    [GeneratedRegex(@"(?<=\s|^)\$\.(?=[rRpP]\.)")]
    private static partial Regex DollarLegalFormRegex();

    [GeneratedRegex(@"(?<=\b[A-Z]\.)0(?=\.)")]
    private static partial Regex ZeroInAcronymRegex();

    /// <summary>Legal forms (Polish and foreign). Short forms are matched only as separate words.</summary>
    [GeneratedRegex(@"(?<![\p{L}.])(sp\.\s?z\s?o\.\s?o\.?|S\.A\.|SA|S\.p\.A\.?|SpA|SPA|S\.r\.l\.?|Srl|SRL|s\.r\.l\.?|s\.r\.o\.?|S\.R\.O\.?|spol\.\s?s\s?r\.\s?o\.?|GmbH|GMBH|AG|B\.V\.?|BV|N\.V\.?|NV|Ltd\.?|LTD\.?|Limited|LLC|Inc\.?|Kft\.?|S\.L\.|SAS|SARL|Oy|AB|A/S|sp\.\s?[jk]\.?|s\.c\.)(?![\p{L}])")]
    private static partial Regex LegalFormRegex();

    [GeneratedRegex(@"NIP[\s.:]*(?:PL)?\s*(?<nip>[\d\s\-]{10,16})", RegexOptions.IgnoreCase)]
    private static partial Regex LabelledNipRegex();

    [GeneratedRegex(@"(?<!\d)(?:\d[\s\-]?){9}\d(?!\d)")]
    private static partial Regex NipCandidateRegex();

    /// <summary>"+39 333 1234567", "+31 (0)72 8200 120", "+420222 111333" – a country code and 7+ more digits on one line.</summary>
    [GeneratedRegex(@"\+ ?\d[\d \-./()]{6,22}\d")]
    private static partial Regex InternationalPhoneRegex();

    [GeneratedRegex(@"(?<![\d\-])(?:\(?\d{2,3}\)?[\s\-]?)?\d{3}[\s\-]?\d{2,3}[\s\-]?\d{2,3}(?![\d\-])")]
    private static partial Regex PolishPhoneRegex();

    [GeneratedRegex(@"\b(mob(ile|il)?|kom(órka|orka)?|tel\.?\s?kom|cell|gsm)\b\.?\s*[:.]?", RegexOptions.IgnoreCase)]
    private static partial Regex MobileLabelRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(?:https?://)?www\.[A-Za-z0-9.\-]+\.[A-Za-z]{2,}(?:/\S*)?", RegexOptions.IgnoreCase)]
    private static partial Regex WwwRegex();

    [GeneratedRegex(@"^https?://")]
    private static partial Regex SchemeRegex();

    /// <summary>PL "00-000", NL "1812 RS", CZ/SK "252 68", IT/DE/FR "25024" followed by the city.</summary>
    [GeneratedRegex(@"(?<![\d+\-])(?<code>\d{2}\s?-\s?\d{3}|\d{4}\s?[A-Z]{2}(?=\s)|\d{3}\s\d{2}|\d{5})\s+(?<city>\p{L}[^\d\n]{0,60})")]
    private static partial Regex PostalCityRegex();

    [GeneratedRegex(@"^\d{2}\s?-\s?\d{3}$")]
    private static partial Regex PolishPostalRegex();

    [GeneratedRegex(@"\b(tel|telef|phone|mobile|mobil|mob|cell|fax|kom|e-?mail|www|nip|vat|cf|pi)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LabelWordRegex();

    /// <summary>"Address", "Siedziba", Italian "Sede Oper.:" / "Sede legale" / "Indirizzo" before the street.</summary>
    [GeneratedRegex(@"^\s*(address|adres|office\s*&\s*warehouse|siedziba|headquarters|indirizzo|sede(\s+(oper\.?|operativa|legale|amministrativa))?)\s*:?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex AddressLabelRegex();

    /// <summary>Street name followed by a house number: "Via Roma, 15", "ul. Polna 12/3", "Tulpstraat 1".</summary>
    [GeneratedRegex(@"^(?<street>(?=.*\p{L}{3})[\p{L}.,'\- ]+\s\d+[a-zA-Z]?(?:/\w+)?)(?=$|[\s,])")]
    private static partial Regex StreetWithNumberRegex();

    [GeneratedRegex(@"^(ul\.|al\.|pl\.|os\.|ulica|aleja|via|viale|piazza|corso|str\.|straße|strasse|rue)\s", RegexOptions.IgnoreCase)]
    private static partial Regex StreetPrefixRegex();

    /// <summary>2–3 words, each "Capitalised" or "ALLCAPS": "Jan Kowalski", "Marco ROSSINI", "Łukasz Wiśniewski".</summary>
    [GeneratedRegex(@"^\p{Lu}(\p{Ll}+|\p{Lu}+)(-\p{Lu}(\p{Ll}+|\p{Lu}+))?(\s\p{Lu}(\p{Ll}+|\p{Lu}+)(-\p{Lu}(\p{Ll}+|\p{Lu}+))?){1,2}$")]
    private static partial Regex PersonRegex();

    [GeneratedRegex(@"\S\s+(?<name>\p{Lu}{2,}\s\p{Lu}{2,})\s*$")]
    private static partial Regex TrailingCapsNameRegex();
}

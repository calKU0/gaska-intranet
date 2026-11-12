using Gaska.Intranet.Core.Text;

namespace Gaska.Intranet.Core.Crm;

/// <summary>Why a contractor was found as a possible match.</summary>
[Flags]
public enum MatchReason
{
    None = 0,

    // Strong – identify the company on their own.
    Nip = 1,
    Email = 2,
    Name = 4,

    // Weak – shared or reused (a group's common domain, a switchboard number, a person who changed jobs).
    Domain = 8,
    Phone = 16,
    Person = 32,
}

/// <summary>A contractor found by <see cref="IContractorService.FindMatchesAsync"/> and the signals that led to it.</summary>
public sealed record ContractorCandidate(ContractorListItem Item, MatchReason Reasons);

/// <summary>
/// Orders and filters possible matches of a business card / a new contractor. Strong signals (NIP, the exact e-mail,
/// the company name or its acronym) win. Only when there are none, weak signals (e-mail domain or website, phone,
/// contact person) count – and for a business card lookup only when the company name agrees at least partly, or when
/// several weak signals point to the same contractor.
/// </summary>
public static class ContractorMatching
{
    private const MatchReason Strong = MatchReason.Nip | MatchReason.Email | MatchReason.Name;

    /// <summary>Least <see cref="NameSimilarity"/> that confirms a weak match ("choć trochę").</summary>
    public const double ConfirmingSimilarity = 0.3;

    public static IReadOnlyList<ContractorListItem> Rank(IEnumerable<ContractorCandidate> candidates, string? name, bool verifyWeakMatches)
    {
        var merged = candidates
            .GroupBy(c => c.Item.Id)
            .Select(g => (Item: g.First().Item, Reasons: g.Aggregate(MatchReason.None, (r, c) => r | c.Reasons)))
            .Select(c => (c.Item, c.Reasons, Similarity: Math.Max(NameSimilarity(name, c.Item.FullName), NameSimilarity(name, c.Item.ShortName))))
            .ToList();

        var strong = merged.Where(c => (c.Reasons & Strong) != 0).ToList();
        var chosen = strong.Count > 0
            ? strong
            : merged.Where(c => !verifyWeakMatches || IsConfirmed(c.Reasons, c.Similarity, name)).ToList();

        return chosen
            .OrderByDescending(c => Score(c.Reasons, c.Similarity))
            .ThenBy(c => c.Item.ShortName, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => c.Item)
            .Take(20)
            .ToList();
    }

    /// <summary>
    /// How much two company names agree, 0–1: the share of the shorter name's distinctive words found in the other
    /// (a word also counts with one typo, or as the start of a longer word); acronyms written with dots or spaces
    /// ("X.Y.Z.A." = "XYZA") are compared letter by letter.
    /// </summary>
    public static double NameSimilarity(string? a, string? b)
    {
        var x = CompanyNames.Core(a);
        var y = CompanyNames.Core(b);
        var compactX = CompanyNames.Compact(a);
        var compactY = CompanyNames.Compact(b);
        if ((compactX.Length >= 2 && compactX == compactY)
            || (Math.Min(compactX.Length, compactY.Length) >= 4 && (compactX.Contains(compactY) || compactY.Contains(compactX))))
        {
            return 1;
        }

        var wordsX = x.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 2).Distinct().ToList();
        var wordsY = y.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 2).Distinct().ToList();
        if (wordsX.Count == 0 || wordsY.Count == 0)
        {
            return 0;
        }

        var (shorter, longer) = wordsX.Count <= wordsY.Count ? (wordsX, wordsY) : (wordsY, wordsX);
        var found = shorter.Count(w => longer.Any(o => SameWord(w, o)));
        return (double)found / shorter.Count;
    }

    /// <summary>Same person? All words of the shorter name appear in the other, in any order ("Kowalski Jan" = "Jan Kowalski").</summary>
    public static bool SamePerson(string? a, string? b)
    {
        var x = TextSearch.Normalize(a ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1).ToList();
        var y = TextSearch.Normalize(b ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1).ToList();
        if (x.Count < 2 || y.Count < 2)
        {
            return false; // a lone first name or surname is not enough
        }

        var (shorter, longer) = x.Count <= y.Count ? (x, y) : (y, x);
        return shorter.All(longer.Contains);
    }

    private static bool IsConfirmed(MatchReason reasons, double similarity, string? name)
    {
        // Two different weak signals (e.g. the website and the person) agree – enough without the name.
        if (System.Numerics.BitOperations.PopCount((uint)reasons) >= 2)
        {
            return true;
        }

        // No company name on the card – nothing to compare; a domain still identifies the company well.
        if (string.IsNullOrWhiteSpace(CompanyNames.Core(name)) && CompanyNames.Compact(name).Length < 2)
        {
            return reasons.HasFlag(MatchReason.Domain);
        }

        return similarity >= ConfirmingSimilarity;
    }

    private static double Score(MatchReason reasons, double similarity)
    {
        var score = similarity * 30;
        if (reasons.HasFlag(MatchReason.Nip)) score += 100;
        if (reasons.HasFlag(MatchReason.Email)) score += 80;
        if (reasons.HasFlag(MatchReason.Name)) score += 60;
        if (reasons.HasFlag(MatchReason.Domain)) score += 40;
        if (reasons.HasFlag(MatchReason.Phone)) score += 30;
        if (reasons.HasFlag(MatchReason.Person)) score += 25;
        return score;
    }

    private static bool SameWord(string a, string b)
    {
        if (a == b)
        {
            return true;
        }

        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        if (shorter.Length >= 4 && longer.StartsWith(shorter, StringComparison.Ordinal))
        {
            return true; // "ferrov" / "ferroviarie", "intern" / "international"
        }

        return shorter.Length >= 5 && longer.Length - shorter.Length <= 1 && Distance(a, b) <= 1;
    }

    private static int Distance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            previous = current;
        }

        return previous[b.Length];
    }
}

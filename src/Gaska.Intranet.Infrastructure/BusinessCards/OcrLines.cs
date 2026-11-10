namespace Gaska.Intranet.Infrastructure.BusinessCards;

/// <summary>Filtering and de-duplication of OCR lines.</summary>
internal static class OcrLines
{
    /// <summary>Drops background noise ("ag", "¢ “te 7 ”") – real card lines are confident and have some letters/digits.</summary>
    /// Contact lines (e-mail, www, phone) are kept even when less certain – they are the most valuable and the parser
    /// validates them anyway.
    public static bool IsMeaningful(string line, float confidence, float minConfidence)
    {
        if (line.Count(char.IsLetterOrDigit) < 3)
        {
            return false;
        }

        var contact = line.Contains('@') || line.Contains("www", StringComparison.OrdinalIgnoreCase) || line.Count(char.IsDigit) >= 7;
        return confidence >= (contact ? Math.Min(minConfidence, 30) : minConfidence);
    }

    /// <summary>The same card line read twice (possibly with a few different characters).</summary>
    public static bool SameText(string a, string b)
    {
        var x = Key(a);
        var y = Key(b);
        if (x.Length == 0 || y.Length == 0)
        {
            return x == y;
        }

        // A shorter reading of the same line ("+39 333 1" vs "+39 333 1234567") – but not a name that merely
        // appears inside another line ("Lin Zhao" inside "E-mail: linzhao@klcz.cz").
        var (shorter, longer) = x.Length <= y.Length ? (x, y) : (y, x);
        if (longer.StartsWith(shorter, StringComparison.Ordinal) || (longer.Contains(shorter) && shorter.Length * 10 >= longer.Length * 6))
        {
            return true;
        }

        return Distance(x, y) <= Math.Max(x.Length, y.Length) / 5;
    }

    private static string Key(string text) =>
        new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}

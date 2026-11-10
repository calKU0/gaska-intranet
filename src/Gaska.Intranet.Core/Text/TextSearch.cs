using System.Globalization;
using System.Text;

namespace Gaska.Intranet.Core.Text;

/// <summary>
/// Text search that ignores case and Polish diacritics ("lukasz" finds "Łukasz").
/// </summary>
public static class TextSearch
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            // "ł" has no Unicode decomposition into a letter + diacritic.
            sb.Append(c == 'ł' ? 'l' : c);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Returns <c>true</c> when every word of the query occurs in any of the fields.
    /// </summary>
    public static bool Matches(string? query, params string?[] fields)
    {
        var terms = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
        {
            return true;
        }

        var haystack = string.Join(' ', fields.Select(Normalize));
        return terms.All(t => haystack.Contains(t, StringComparison.Ordinal));
    }
}

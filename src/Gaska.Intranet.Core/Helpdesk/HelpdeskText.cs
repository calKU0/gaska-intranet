using System.Net;
using System.Text.RegularExpressions;

namespace Gaska.Intranet.Core.Helpdesk;

/// <summary>
/// GLPI keeps ticket texts as HTML (sometimes HTML-encoded once more). The intranet shows them as plain text –
/// line breaks and list items kept, every tag dropped – so nothing from a ticket can run script in the page.
/// </summary>
public static partial class HelpdeskText
{
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var text = html;

        // Stored encoded ("&lt;p&gt;…") → real tags first.
        if (text.Contains("&lt;", StringComparison.Ordinal) && !text.Contains('<'))
        {
            text = WebUtility.HtmlDecode(text);
        }

        text = DroppedBlocks().Replace(text, string.Empty);
        text = LineBreak().Replace(text, "\n");
        text = ListItem().Replace(text, "\n• ");
        text = BlockEnd().Replace(text, "\n");
        text = Tag().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text).Replace(' ', ' ');
        text = TrailingSpaces().Replace(text, "\n");
        text = ManyNewLines().Replace(text, "\n\n");
        return text.Trim();
    }

    [GeneratedRegex(@"<(script|style|head)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DroppedBlocks();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"</(p|div|h[1-6]|tr|table|ul|ol|blockquote|pre)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex TrailingSpaces();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyNewLines();
}

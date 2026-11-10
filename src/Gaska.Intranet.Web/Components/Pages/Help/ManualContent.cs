using System.Text.RegularExpressions;

namespace Gaska.Intranet.Web.Components.Pages.Help;

/// <summary>
/// The user manual: Help/instrukcja.html (our own trusted HTML, no scripts) shown on /instrukcja, and the PDF built
/// from the same file by tools/build-manual.py (Help/Instrukcja-Gaska-Intranet.pdf).
/// </summary>
public static partial class ManualContent
{
    public const string PdfRoute = "/instrukcja/pdf";
    public const string PdfFileName = "Instrukcja-Gaska-Intranet.pdf";

    private static readonly Lazy<string> _html = new(() => Load("instrukcja.html"));

    public static string Html => _html.Value;

    /// <summary>Chapters (section id → heading) for the table of contents.</summary>
    public static IReadOnlyList<(string Id, string Title)> Sections { get; } =
        SectionHeading().Matches(_html.Value).Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();

    public static string PdfPath => Path.Combine(HelpDirectory, PdfFileName);

    // Help/ is copied next to the application (see the .csproj); during development it is in the project folder.
    private static string HelpDirectory =>
        Directory.Exists(Path.Combine(AppContext.BaseDirectory, "Help"))
            ? Path.Combine(AppContext.BaseDirectory, "Help")
            : Path.Combine(Directory.GetCurrentDirectory(), "Help");

    private static string Load(string fileName)
    {
        var path = Path.Combine(HelpDirectory, fileName);
        return File.Exists(path) ? File.ReadAllText(path) : "<p>Instrukcja jest chwilowo niedostępna.</p>";
    }

    [GeneratedRegex(@"<section id=""([^""]+)"">\s*<h2>(?:\d+\.\s*)?([^<]+)</h2>")]
    private static partial Regex SectionHeading();
}

using Gaska.Intranet.Core.Crm;
using Gaska.Intranet.Infrastructure.BusinessCards;
using Microsoft.Extensions.Logging.Abstractions;
using Options = Microsoft.Extensions.Options.Options;

namespace Gaska.Intranet.Tests;

/// <summary>
/// OCR on real business card photos from TestData/cards (git-ignored – personal data). Skipped when the folder is empty.
/// Writes the raw OCR text and parsed fields to TestData/cards/ocr-report.txt for diagnostics.
/// </summary>
[Trait("Category", "Samples")]
public class BusinessCardSampleTests
{
    private static readonly string Tessdata = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Gaska.Intranet.Web", "tessdata"));

    private static readonly string Cards = Path.Combine(AppContext.BaseDirectory, "TestData", "cards");

    [Fact]
    public async Task Dump_ocr_of_sample_cards()
    {
        if (!Directory.Exists(Cards))
        {
            return;
        }

        using var reader = new TesseractBusinessCardReader(
            Options.Create(new BusinessCardOptions { TessdataPath = Tessdata }),
            NullLogger<TesseractBusinessCardReader>.Instance);

        var report = new System.Text.StringBuilder();
        foreach (var file in Directory.GetFiles(Cards, "*.jpg").Order())
        {
            await using var photo = File.OpenRead(file);
            var card = await reader.ReadAsync(photo);
            report.AppendLine($"===== {Path.GetFileName(file)}");
            report.AppendLine(Describe(card));
            report.AppendLine("--- raw");
            report.AppendLine(card.RawText);
        }

        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData", "cards", "ocr-report.txt"));
        await File.WriteAllTextAsync(output, report.ToString());
    }

    internal static string Describe(BusinessCardData card) =>
        $"""
        Company: {card.CompanyName}
        Person:  {card.PersonName}
        Position:{card.Position}
        NIP:     {card.Nip}
        Emails:  {string.Join("; ", card.Emails)}
        Phones:  {string.Join("; ", card.Phones)}
        Www:     {card.Www}
        Address: {card.Address} | {card.PostalCode} | {card.City}
        """;
}

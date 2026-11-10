using Gaska.Intranet.Infrastructure.BusinessCards;
using Microsoft.Extensions.Logging.Abstractions;
using Options = Microsoft.Extensions.Options.Options;

namespace Gaska.Intranet.Tests;

/// <summary>End-to-end OCR on a synthetic, slightly rotated and blurred business card photo.</summary>
public class BusinessCardOcrTests
{
    private static readonly string Tessdata = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Gaska.Intranet.Web", "tessdata"));

    [Fact]
    public async Task Reads_polish_business_card_photo()
    {
        Assert.True(File.Exists(Path.Combine(Tessdata, "pol.traineddata")), $"Missing tessdata in {Tessdata}");

        using var reader = new TesseractBusinessCardReader(
            Options.Create(new BusinessCardOptions { TessdataPath = Tessdata }),
            NullLogger<TesseractBusinessCardReader>.Instance);
        await using var photo = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TestData", "business-card-sample.jpg"));

        var card = await reader.ReadAsync(photo);

        var diagnostics = $"OCR text:\n{card.RawText}\nEmails: {string.Join(";", card.Emails)}\nPhones: {string.Join(";", card.Phones)}\nCompany: {card.CompanyName}\nPerson: {card.PersonName}";
        Assert.True(card.Nip == "1234563218", diagnostics);
        // The tiny dot after the initial may be lost on this deliberately blurred photo.
        Assert.True(card.Emails.Any(e => e is "l.wisniewski@agro-serwis.pl" or "lwisniewski@agro-serwis.pl"), diagnostics);
        Assert.True(card.Phones.Contains("601 222 333"), diagnostics);
        Assert.Equal("16-080", card.PostalCode);
        Assert.Equal("Tykocin", card.City);
        Assert.Contains("AGRO-SERWIS", card.CompanyName);
        Assert.Equal("Łukasz Wiśniewski", card.PersonName);
    }
}

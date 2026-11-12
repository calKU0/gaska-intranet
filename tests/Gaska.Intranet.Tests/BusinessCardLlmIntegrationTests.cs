using Gaska.Intranet.Infrastructure.BusinessCards;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;
using Options = Microsoft.Extensions.Options.Options;

namespace Gaska.Intranet.Tests;

/// <summary>
/// The vision language model on the Ollama server from appsettings.Local.json ("BusinessCards" section). Does nothing
/// when no model is configured. GASKA_CARD_PHOTO=&lt;path&gt; reads that photo instead of the synthetic sample and
/// prints the result (nothing is asserted for it).
/// </summary>
public class BusinessCardLlmIntegrationTests(ITestOutputHelper output)
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public async Task Language_model_reads_business_card()
    {
        var options = new BusinessCardOptions { TessdataPath = Path.Combine(Root, "src", "Gaska.Intranet.Web", "tessdata") };
        new ConfigurationBuilder().AddJsonFile(Path.Combine(Root, "src", "Gaska.Intranet.Web", "appsettings.Local.json"), optional: true).Build()
            .GetSection(BusinessCardOptions.SectionName).Bind(options);
        if (string.IsNullOrWhiteSpace(options.LlmEndpoint))
        {
            return;
        }

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var llm = new OllamaCardReader(http, Options.Create(options));
        using var ocr = new TesseractBusinessCardReader(Options.Create(options), NullLogger<TesseractBusinessCardReader>.Instance);
        var reader = new BusinessCardReader(llm, ocr, Options.Create(options), NullLogger<BusinessCardReader>.Instance);

        var own = Environment.GetEnvironmentVariable("GASKA_CARD_PHOTO");
        await using var photo = File.OpenRead(own ?? Path.Combine(AppContext.BaseDirectory, "TestData", "business-card-sample.jpg"));
        var started = DateTime.UtcNow;
        var card = await reader.ReadAsync(photo);

        output.WriteLine($"{(DateTime.UtcNow - started).TotalSeconds:F1} s");
        output.WriteLine($"Firma: {card.CompanyName}\nOsoba: {card.PersonName} ({card.Position})\nTelefony: {string.Join(", ", card.Phones)}\n" +
                         $"E-mail: {string.Join(", ", card.Emails)}\nWWW: {card.Www}\nAdres: {card.Address}, {card.PostalCode} {card.City}\n" +
                         $"Kraj: {card.Country}\nNIP: {card.Nip}\n---\n{card.RawText}");
        if (own is not null)
        {
            return;
        }

        Assert.StartsWith("Model językowy", card.RawText); // the model answered – not the OCR fallback
        Assert.Equal("1234563218", card.Nip);
        Assert.Equal("Łukasz Wiśniewski", card.PersonName);
        Assert.Contains("601222333", card.Phones); // compact, as stored in GoNet
        Assert.Equal("16-080", card.PostalCode);
        Assert.Equal("Tykocin", card.City);
    }
}

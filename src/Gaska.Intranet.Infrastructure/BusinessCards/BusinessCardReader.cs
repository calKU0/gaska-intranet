using System.Diagnostics;
using System.Text;
using Gaska.Intranet.Core.Crm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Infrastructure.BusinessCards;

/// <summary>
/// Reads a business card in up to three ways, all inside the company network:
/// <list type="number">
/// <item>the QR code, when the card has one with a vCard/MeCard – exact data, preferred field by field;</item>
/// <item>the vision language model on the company Ollama server (when configured) – the rest of the card;</item>
/// <item>Tesseract OCR – only when the model is not configured, unreachable or too slow.</item>
/// </list>
/// </summary>
internal sealed class BusinessCardReader(
    OllamaCardReader llm,
    TesseractBusinessCardReader ocr,
    IOptions<BusinessCardOptions> options,
    ILogger<BusinessCardReader> logger) : IBusinessCardReader
{
    private readonly BusinessCardOptions _options = options.Value;

    public async Task<BusinessCardData> ReadAsync(Stream image, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await image.CopyToAsync(buffer, ct);
        if (buffer.Length > _options.MaxImageBytes)
        {
            throw new CrmException("Zdjęcie jest za duże.");
        }

        var bytes = buffer.ToArray();
        // The QR code is read on the server while the model reads the photo – it never delays the answer.
        var qrTask = Task.Run(() => ReadQrCode(bytes), ct);
        var fromCard = await ReadPhotoAsync(bytes, ct);
        var qr = await qrTask;

        var card = qr is null
            ? fromCard
            : BusinessCardParser.Merge(BusinessCardParser.FromFields(qr.Value.Fields, $"Kod QR:\n{qr.Value.Text}"), fromCard);

        // Phones go to GoNet without spaces and dashes.
        return card with { Phones = card.Phones.Select(BusinessCardParser.CompactPhone).Where(p => p.Length > 0).Distinct().ToList() };
    }

    private (string Text, BusinessCardFields Fields)? ReadQrCode(byte[] bytes)
    {
        try
        {
            var text = CardQrCode.Read(bytes);
            var fields = ContactCode.Parse(text);
            logger.LogInformation("Business card QR code: {Result}", text is null ? "none" : fields is null ? "no contact data" : "contact data");
            return fields is null ? null : (text!, fields);
        }
        catch (Exception ex)
        {
            // The QR code is a bonus – the photo is still read.
            logger.LogWarning(ex, "Business card QR code could not be read");
            return null;
        }
    }

    private async Task<BusinessCardData> ReadPhotoAsync(byte[] bytes, CancellationToken ct)
    {
        if (llm.IsConfigured)
        {
            var watch = Stopwatch.StartNew();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.LlmTimeoutSeconds));
            try
            {
                var fields = await llm.ReadAsync(bytes, timeout.Token);
                logger.LogInformation("Business card read by the language model in {Seconds:F1} s", watch.Elapsed.TotalSeconds);
                return BusinessCardParser.FromFields(fields, Describe(fields));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Language model did not read the business card ({Seconds:F1} s) – falling back to OCR", watch.Elapsed.TotalSeconds);
            }
        }

        using var stream = new MemoryStream(bytes, writable: false);
        return await ocr.ReadAsync(stream, ct);
    }

    /// <summary>What the model returned, for "Pokaż rozpoznany tekst".</summary>
    private static string Describe(BusinessCardFields f)
    {
        var text = new StringBuilder("Model językowy:\n");
        void Line(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                text.Append(label).Append(": ").AppendLine(value.Trim());
            }
        }

        Line("Firma", f.Company);
        Line("Osoba", $"{f.FirstName} {f.LastName}");
        Line("Stanowisko", f.Position);
        Line("Komórka", f.Mobile);
        Line("Telefony", string.Join(", ", f.Phones));
        Line("E-mail", string.Join(", ", f.Emails));
        Line("WWW", f.Website);
        Line("Adres", $"{f.Street}, {f.PostalCode} {f.City}".Trim(' ', ','));
        Line("Kraj", f.Country);
        Line("NIP", f.Nip);
        return text.ToString().TrimEnd();
    }
}

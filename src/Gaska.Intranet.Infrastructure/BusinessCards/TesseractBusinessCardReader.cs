using Gaska.Intranet.Core.Crm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tesseract;

namespace Gaska.Intranet.Infrastructure.BusinessCards;

public sealed class BusinessCardOptions
{
    public const string SectionName = "BusinessCards";

    /// <summary>Folder with *.traineddata files (relative to the application folder).</summary>
    public string TessdataPath { get; set; } = "tessdata";

    public string Languages { get; set; } = "pol+eng";

    /// <summary>Largest accepted photo.</summary>
    public long MaxImageBytes { get; set; } = 15 * 1024 * 1024;

    /// <summary>Ollama chat endpoint (e.g. http://host:11434/api/chat); empty = no language model, OCR only.</summary>
    public string LlmEndpoint { get; set; } = string.Empty;

    /// <summary>Ollama model that accepts images (capability "vision").</summary>
    public string LlmModel { get; set; } = string.Empty;

    /// <summary>After this the photo is read with OCR instead.</summary>
    public int LlmTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Context size sent with every request (num_ctx); 0 = the server's default. Must equal the size other clients of the
    /// same Ollama server use for this model – a different size makes Ollama reload the whole model (a minute or more).
    /// </summary>
    public int LlmContextLength { get; set; }

    /// <summary>Longer side of the photo sent to the model, in pixels.</summary>
    public int LlmImageMaxSide { get; set; } = 1280;
}

/// <summary>
/// Free, local OCR (Tesseract) – the photo never leaves the server. Fallback when the language model is not available. The engine is not thread-safe and expensive to
/// create, so one instance is shared and calls are serialised.
/// </summary>
internal sealed class TesseractBusinessCardReader : IBusinessCardReader, IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Lazy<TesseractEngine> _engine;
    private readonly BusinessCardOptions _options;
    private readonly ILogger<TesseractBusinessCardReader> _logger;

    public TesseractBusinessCardReader(IOptions<BusinessCardOptions> options, ILogger<TesseractBusinessCardReader> logger)
    {
        _options = options.Value;
        _logger = logger;
        _engine = new Lazy<TesseractEngine>(() =>
        {
            var path = Path.IsPathRooted(_options.TessdataPath)
                ? _options.TessdataPath
                : Path.Combine(AppContext.BaseDirectory, _options.TessdataPath);
            return new TesseractEngine(path, _options.Languages, EngineMode.Default);
        });
    }

    public async Task<BusinessCardData> ReadAsync(Stream image, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await image.CopyToAsync(buffer, ct);
        if (buffer.Length > _options.MaxImageBytes)
        {
            throw new CrmException("Zdjęcie jest za duże.");
        }

        await _lock.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                var bytes = buffer.ToArray();
                var text = Recognize(bytes, evenOutLighting: false);
                var card = BusinessCardParser.Parse(text);

                // No e-mail or postal code read – often a shadow across the card. Read again with the lighting evened
                // out (only then: on well-lit cards the extra lines would only add noise).
                if (card.Emails.Count == 0 || card.PostalCode is null)
                {
                    text = Recognize(bytes, evenOutLighting: true);
                    card = BusinessCardParser.Parse(text);
                }

                _logger.LogInformation("Business card OCR: {Lines} lines", text.Count(c => c == '\n'));
                return card;
            }, ct);
        }
        catch (Exception ex) when (ex is not (CrmException or OperationCanceledException))
        {
            _logger.LogError(ex, "Business card OCR failed");
            throw new CrmException("Nie udało się odczytać zdjęcia. Zrób zdjęcie z bliska, przy dobrym świetle, na wprost.", ex);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Several passes, each adding only the lines the earlier ones missed (only confident lines are kept):
    /// (1) the card cut out of the photo and enlarged 2× – small print is read far better; (2) optionally the same with
    /// the lighting evened out – a shadow across the card otherwise loses whole lines; (3) coloured panels on their own;
    /// (4) the whole photo – sometimes reads text the others merge with logos.
    /// </summary>
    internal string Recognize(byte[] image, bool evenOutLighting)
    {
        using var rgb = Pix.LoadFromMemory(image);
        using var gray = rgb.Depth == 32 ? rgb.ConvertRGBToGray() : rgb.ConvertTo8(0);
        var card = CardImage.FindCard(rgb, gray);

        using var enlarged = gray.Scale(2f, 2f);
        var region = new Rect(card.X1 * 2, card.Y1 * 2, card.Width * 2, card.Height * 2);
        var lines = ReadLines(enlarged, region, minConfidence: 55);

        void AddMissing(IEnumerable<string> extra)
        {
            foreach (var line in extra)
            {
                if (!lines.Any(existing => OcrLines.SameText(existing, line)))
                {
                    lines.Add(line);
                }
            }
        }

        if (evenOutLighting)
        {
            using var even = CardImage.EvenOutLighting(gray);
            using var evenEnlarged = even.Scale(2f, 2f);
            var shaded = CardImage.FindCard(rgb, gray, shadowTolerant: true);
            AddMissing(ReadLines(evenEnlarged, new Rect(shaded.X1 * 2, shaded.Y1 * 2, shaded.Width * 2, shaded.Height * 2), minConfidence: 55));
        }

        // Coloured panels read on their own (white text on a blue band is often lost in the whole-card pass).
        foreach (var panel in CardImage.FindColorPanels(rgb, card))
        {
            AddMissing(ReadLines(enlarged, new Rect(panel.X1 * 2, panel.Y1 * 2, panel.Width * 2, panel.Height * 2), minConfidence: 60));
        }

        AddMissing(ReadLines(rgb, new Rect(0, 0, rgb.Width, rgb.Height), minConfidence: 75));

        return string.Join('\n', lines);
    }

    private List<string> ReadLines(Pix image, Rect region, float minConfidence)
    {
        var lines = new List<string>();
        // Tesseract's region option misreads some regions that start at the left edge of the image (it read the grid
        // paper below the card instead of the card) – those get an image cut to the region instead.
        var whole = region.X1 == 0 && region.Y1 == 0 && region.Width >= image.Width && region.Height >= image.Height;
        using var part = region.X1 == 0 && !whole ? CardImage.Crop(image, region) : null;
        using var page = part is null
            ? _engine.Value.Process(image, region, PageSegMode.Auto)
            : _engine.Value.Process(part, PageSegMode.Auto);
        using var iterator = page.GetIterator();
        iterator.Begin();
        do
        {
            var line = iterator.GetText(PageIteratorLevel.TextLine)?.Trim();
            var confidence = iterator.GetConfidence(PageIteratorLevel.TextLine);
            if (!string.IsNullOrEmpty(line) && OcrLines.IsMeaningful(line, confidence, minConfidence))
            {
                lines.Add(line);
            }
        }
        while (iterator.Next(PageIteratorLevel.TextLine));

        return lines;
    }

    public void Dispose()
    {
        if (_engine.IsValueCreated)
        {
            _engine.Value.Dispose();
        }

        _lock.Dispose();
    }
}

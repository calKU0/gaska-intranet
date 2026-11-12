using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Gaska.Intranet.Core.Crm;
using Microsoft.Extensions.Options;
using Tesseract;

namespace Gaska.Intranet.Infrastructure.BusinessCards;

/// <summary>
/// Reads a card photo with a vision language model on the company's own Ollama server (POST /api/chat with the image
/// and a JSON schema for the answer). The photo stays inside the company network.
/// </summary>
internal sealed class OllamaCardReader(HttpClient http, IOptions<BusinessCardOptions> options)
{
    private const string Prompt =
        """
        Read this business card photo and fill in the JSON fields.
        Rules:
        - Copy names, company, position and address exactly as printed, keeping diacritics.
        - emails: every e-mail address printed anywhere on the card (personal and general ones, e.g. info@), exactly as printed; they never contain accented letters. Same for the website.
        - mobile: the mobile/cell number; phones: every other phone number (no fax numbers). Keep the country code as printed.
        - country: the country name in Polish (e.g. "Francja", "Niemcy", "Włochy"); null for Poland or when not printed.
        - nip: Polish tax number (NIP) only, when printed.
        - Use null for anything that is not on the card. Do not guess.
        """;

    private static readonly JsonObject Schema = BuildSchema();

    private readonly BusinessCardOptions _options = options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.LlmEndpoint) && !string.IsNullOrWhiteSpace(_options.LlmModel);

    public async Task<BusinessCardFields> ReadAsync(byte[] image, CancellationToken ct)
    {
        var request = new JsonObject
        {
            ["model"] = _options.LlmModel,
            ["stream"] = false,
            ["think"] = false,
            ["format"] = Schema.DeepClone(),
            ["options"] = new JsonObject { ["temperature"] = 0 },
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = Prompt,
                ["images"] = new JsonArray(Convert.ToBase64String(Shrink(image, _options.LlmImageMaxSide))),
            }),
        };

        using var response = await http.PostAsJsonAsync(_options.LlmEndpoint, request, ct);
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<ChatReply>(ct);
        var content = reply?.Message?.Content ?? throw new JsonException("Empty answer from the model.");
        return JsonSerializer.Deserialize<CardAnswer>(content)?.ToFields() ?? throw new JsonException("Unreadable answer from the model.");
    }

    /// <summary>
    /// A phone photo (12+ Mpx) costs the model thousands of image tokens and much time without reading any better –
    /// it gets a JPEG with the longer side limited.
    /// </summary>
    internal static byte[] Shrink(byte[] image, int maxSide)
    {
        using var pix = Pix.LoadFromMemory(image);
        var side = Math.Max(pix.Width, pix.Height);
        if (side <= maxSide && image.Length < 2 * 1024 * 1024)
        {
            return image;
        }

        var scale = Math.Min(1f, (float)maxSide / side);
        using var small = scale < 1f ? pix.Scale(scale, scale) : null;
        // Leptonica writes only to files.
        var path = Path.Combine(Path.GetTempPath(), $"gaska-card-{Guid.NewGuid():N}.jpg");
        try
        {
            (small ?? pix).Save(path, ImageFormat.JfifJpeg);
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static JsonObject BuildSchema()
    {
        static JsonObject Text() => new() { ["type"] = new JsonArray("string", "null") };

        string[] texts = ["company", "firstName", "lastName", "position", "mobile", "website", "street", "postalCode", "city", "country", "nip"];
        var properties = new JsonObject();
        foreach (var name in texts)
        {
            properties[name] = Text();
        }

        properties["phones"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } };
        properties["emails"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } };

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray([.. texts.Concat(["phones", "emails"]).Select(n => JsonValue.Create(n))]),
        };
    }

    private sealed record ChatReply([property: JsonPropertyName("message")] ChatMessage? Message);

    private sealed record ChatMessage([property: JsonPropertyName("content")] string? Content);

    private sealed record CardAnswer(
        [property: JsonPropertyName("company")] string? Company,
        [property: JsonPropertyName("firstName")] string? FirstName,
        [property: JsonPropertyName("lastName")] string? LastName,
        [property: JsonPropertyName("position")] string? Position,
        [property: JsonPropertyName("mobile")] string? Mobile,
        [property: JsonPropertyName("phones")] List<string>? Phones,
        [property: JsonPropertyName("emails")] List<string>? Emails,
        [property: JsonPropertyName("website")] string? Website,
        [property: JsonPropertyName("street")] string? Street,
        [property: JsonPropertyName("postalCode")] string? PostalCode,
        [property: JsonPropertyName("city")] string? City,
        [property: JsonPropertyName("country")] string? Country,
        [property: JsonPropertyName("nip")] string? Nip)
    {
        public BusinessCardFields ToFields() => new()
        {
            Company = Company,
            FirstName = FirstName,
            LastName = LastName,
            Position = Position,
            Mobile = Mobile,
            Phones = Phones ?? [],
            Emails = Emails ?? [],
            Website = Website,
            Street = Street,
            PostalCode = PostalCode,
            City = City,
            Country = Country,
            Nip = Nip,
        };
    }
}

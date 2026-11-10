using System.Net;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Gaska.Intranet.Core.Crm;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Infrastructure.BusinessCards;

public sealed class GusOptions
{
    public const string SectionName = "Gus";

    /// <summary>User key for the GUS BIR 1.1 API (keep it in appsettings.Local.json). Empty = GUS lookups disabled.</summary>
    public string? ApiKey { get; set; }

    public string Endpoint { get; set; } = "https://wyszukiwarkaregon.stat.gov.pl/wsBIR/UslugaBIRzewnPubl.svc";
}

/// <summary>
/// REGON register of Statistics Poland (GUS, API "BIR 1.1"). Covers all businesses – also those that are not VAT payers –
/// and returns the voivodeship. Protocol: SOAP 1.2 with WS-Addressing; a session id from "Zaloguj" goes in the "sid"
/// HTTP header; responses are MTOM, the search result is escaped XML inside the SOAP body.
/// </summary>
internal sealed partial class GusBirRegistry(
    HttpClient http,
    IOptions<GusOptions> options,
    IMemoryCache cache,
    ILogger<GusBirRegistry> logger) : ICompanyRegistry
{
    private const string Namespace = "http://CIS/BIR/PUBL/2014/07";
    private const string SessionCacheKey = "gus:sid";
    private readonly GusOptions _options = options.Value;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public Task<CompanyInfo?> FindByNipAsync(string nip, CancellationToken ct = default) =>
        Nip.Normalize(nip) is { } digits ? SearchAsync("Nip", digits, ct) : Task.FromResult<CompanyInfo?>(null);

    public Task<CompanyInfo?> FindByRegonAsync(string regon, CancellationToken ct = default) =>
        Regon.Normalize(regon) is { } digits ? SearchAsync("Regon", digits, ct) : Task.FromResult<CompanyInfo?>(null);

    private async Task<CompanyInfo?> SearchAsync(string parameter, string value, CancellationToken ct)
    {
        if (!IsEnabled)
        {
            return null;
        }

        var cacheKey = $"gus:{parameter}:{value}";
        if (cache.TryGetValue(cacheKey, out CompanyInfo? cached))
        {
            return cached;
        }

        try
        {
            // An expired session returns an empty result – log in again once.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var sid = await GetSessionAsync(forceNew: attempt > 0, ct);
                var body = $"""
                    <ns:DaneSzukajPodmioty>
                      <ns:pParametryWyszukiwania>
                        <dat:{parameter} xmlns:dat="{Namespace}/DataContract">{SecurityElement.Escape(value)}</dat:{parameter}>
                      </ns:pParametryWyszukiwania>
                    </ns:DaneSzukajPodmioty>
                    """;
                var response = await CallAsync("DaneSzukajPodmioty", body, sid, ct);
                var resultXml = ExtractResult(response, "DaneSzukajPodmiotyResult");
                if (string.IsNullOrWhiteSpace(resultXml))
                {
                    continue;
                }

                var info = ParseSearchResult(resultXml);
                cache.Set(cacheKey, info, TimeSpan.FromHours(info is null ? 1 : 24));
                return info;
            }

            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            logger.LogWarning(ex, "GUS lookup failed ({Parameter})", parameter);
            return null;
        }
    }

    private async Task<string> GetSessionAsync(bool forceNew, CancellationToken ct)
    {
        if (!forceNew && cache.TryGetValue(SessionCacheKey, out string? sid) && !string.IsNullOrEmpty(sid))
        {
            return sid;
        }

        var body = $"<ns:Zaloguj><ns:pKluczUzytkownika>{SecurityElement.Escape(_options.ApiKey)}</ns:pKluczUzytkownika></ns:Zaloguj>";
        var response = await CallAsync("Zaloguj", body, sid: null, ct);
        var newSid = ExtractResult(response, "ZalogujResult");
        if (string.IsNullOrWhiteSpace(newSid))
        {
            throw new HttpRequestException("GUS BIR login failed (check Gus:ApiKey).");
        }

        // Sessions expire after 60 minutes of inactivity.
        cache.Set(SessionCacheKey, newSid, TimeSpan.FromMinutes(45));
        return newSid;
    }

    private async Task<string> CallAsync(string action, string body, string? sid, CancellationToken ct)
    {
        var envelope = $"""
            <soap:Envelope xmlns:soap="http://www.w3.org/2003/05/soap-envelope" xmlns:ns="{Namespace}">
              <soap:Header xmlns:wsa="http://www.w3.org/2005/08/addressing">
                <wsa:To>{_options.Endpoint}</wsa:To>
                <wsa:Action>{Namespace}/IUslugaBIRzewnPubl/{action}</wsa:Action>
              </soap:Header>
              <soap:Body>{body}</soap:Body>
            </soap:Envelope>
            """;

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml"),
        };
        if (sid is not null)
        {
            request.Headers.Add("sid", sid);
        }

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Text of &lt;{element}&gt; in the (MTOM) SOAP response, XML-unescaped.</summary>
    internal static string? ExtractResult(string response, string element)
    {
        var m = Regex.Match(response, $"<{element}>(?<v>.*?)</{element}>", RegexOptions.Singleline);
        return m.Success ? WebUtility.HtmlDecode(m.Groups["v"].Value) : null;
    }

    /// <summary>Parses "&lt;root&gt;&lt;dane&gt;…&lt;/dane&gt;&lt;/root&gt;"; null when GUS reports "not found".</summary>
    internal static CompanyInfo? ParseSearchResult(string xml)
    {
        var dane = XDocument.Parse(xml).Root?.Elements("dane").FirstOrDefault();
        string? Get(string name) => dane?.Element(name)?.Value.Trim() is { Length: > 0 } v ? v : null;

        if (dane is null || Get("ErrorCode") is not null || Get("Nazwa") is not { } name)
        {
            return null;
        }

        var locality = Get("Miejscowosc");
        var postCity = Get("MiejscowoscPoczty") ?? locality;
        var number = Get("NrNieruchomosci") is { } nr ? Get("NrLokalu") is { } lokal ? $"{nr}/{lokal}" : nr : null;

        // Places without streets: "Kolonia Przykładowa 5" – the locality takes the place of the street; the post office is the city.
        var street = Get("Ulica") ?? locality;
        var address = string.Join(' ', new[] { street, number }.Where(p => p is not null));

        return new CompanyInfo(
            Name: name,
            Nip: Get("Nip"),
            Regon: Get("Regon"),
            Address: address.Length > 0 ? address : null,
            PostalCode: Get("KodPocztowy"),
            City: postCity,
            Voivodeship: Get("Wojewodztwo"),
            Source: "GUS");
    }
}

/// <summary>GUS first (all businesses, includes voivodeship), then the VAT white list.</summary>
internal sealed class CompanyRegistry(GusBirRegistry gus, MfWhiteListRegistry whiteList) : ICompanyRegistry
{
    public async Task<CompanyInfo?> FindByNipAsync(string nip, CancellationToken ct = default) =>
        await gus.FindByNipAsync(nip, ct) ?? await whiteList.FindByNipAsync(nip, ct);

    public async Task<CompanyInfo?> FindByRegonAsync(string regon, CancellationToken ct = default) =>
        await gus.FindByRegonAsync(regon, ct) ?? await whiteList.FindByRegonAsync(regon, ct);
}

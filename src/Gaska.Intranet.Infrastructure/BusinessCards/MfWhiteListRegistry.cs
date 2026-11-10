using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Gaska.Intranet.Core.Crm;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Gaska.Intranet.Infrastructure.BusinessCards;

/// <summary>
/// Free public register of VAT payers of the Polish Ministry of Finance ("biała lista", wl-api.mf.gov.pl).
/// Only the NIP is sent. Failures (no network, limits) simply return null – the user fills the form by hand.
/// </summary>
internal sealed partial class MfWhiteListRegistry(HttpClient http, IMemoryCache cache, TimeProvider clock, ILogger<MfWhiteListRegistry> logger) : ICompanyRegistry
{
    public Task<CompanyInfo?> FindByNipAsync(string nip, CancellationToken ct = default) =>
        Nip.Normalize(nip) is { } digits ? SearchAsync("nip", digits, ct) : Task.FromResult<CompanyInfo?>(null);

    public Task<CompanyInfo?> FindByRegonAsync(string regon, CancellationToken ct = default) =>
        Regon.Normalize(regon) is { } digits ? SearchAsync("regon", digits, ct) : Task.FromResult<CompanyInfo?>(null);

    private async Task<CompanyInfo?> SearchAsync(string kind, string value, CancellationToken ct)
    {
        var cacheKey = $"mf:{kind}:{value}";
        if (cache.TryGetValue(cacheKey, out CompanyInfo? cached))
        {
            return cached;
        }

        try
        {
            var date = clock.GetLocalNow().ToString("yyyy-MM-dd");
            var response = await http.GetFromJsonAsync<Response>($"api/search/{kind}/{value}?date={date}", ct);
            var subject = response?.Result?.Subject;
            if (subject?.Name is null)
            {
                return null;
            }

            var (street, postal, city) = SplitAddress(subject.WorkingAddress ?? subject.ResidenceAddress);
            var info = new CompanyInfo(subject.Name, subject.Nip, subject.Regon, street, postal, city, Source: "Biała lista VAT");
            cache.Set(cacheKey, info, TimeSpan.FromDays(1));
            return info;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "VAT white list lookup failed ({Kind})", kind);
            return null;
        }
    }

    /// <summary>"UL. POLNA 1, 00-950 WARSZAWA" → ("UL. POLNA 1", "00-950", "WARSZAWA").</summary>
    internal static (string? Street, string? PostalCode, string? City) SplitAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return (null, null, null);
        }

        var m = PostalCityRegex().Match(address);
        if (!m.Success)
        {
            return (address.Trim(), null, null);
        }

        return (address[..m.Index].Trim(' ', ','), m.Groups["code"].Value, m.Groups["city"].Value.Trim());
    }

    [GeneratedRegex(@"(?<code>\d{2}-\d{3})\s+(?<city>.+)$")]
    private static partial Regex PostalCityRegex();

    private sealed record Response([property: JsonPropertyName("result")] Result? Result);

    private sealed record Result([property: JsonPropertyName("subject")] Subject? Subject);

    private sealed record Subject(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("nip")] string? Nip,
        [property: JsonPropertyName("regon")] string? Regon,
        [property: JsonPropertyName("workingAddress")] string? WorkingAddress,
        [property: JsonPropertyName("residenceAddress")] string? ResidenceAddress);
}

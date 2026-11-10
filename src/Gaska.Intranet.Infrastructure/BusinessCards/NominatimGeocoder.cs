using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Gaska.Intranet.Core.Crm;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Gaska.Intranet.Infrastructure.BusinessCards;

/// <summary>
/// Address → coordinates with OpenStreetMap Nominatim (free). Usage policy: max 1 request per second, an identifying
/// User-Agent and caching – all handled here. Only the company address is sent. Failures return null and never block saving.
/// Nominatim does not understand street prefixes ("ul. Polna 1" fails, "Polna 1" works), so they are removed; when the
/// street cannot be found the postal code + town is used (town-level accuracy).
/// </summary>
internal sealed partial class NominatimGeocoder(HttpClient http, IMemoryCache cache, ILogger<NominatimGeocoder> logger) : IGeocoder
{
    private static readonly SemaphoreSlim Throttle = new(1, 1);
    private static DateTime _lastCall = DateTime.MinValue;

    public async Task<GeoPoint?> GeocodeAsync(string address, CancellationToken ct = default)
    {
        var key = "geo:" + address.Trim().ToUpperInvariant();
        if (cache.TryGetValue(key, out GeoPoint? cached))
        {
            return cached;
        }

        GeoPoint? point = null;
        foreach (var query in Variants(address))
        {
            point = await SearchAsync(query, ct);
            if (point is not null)
            {
                break;
            }
        }

        cache.Set(key, point, TimeSpan.FromDays(point is null ? 1 : 30));
        return point;
    }

    /// <summary>"ul. Polna 1, 16-080 Tykocin, Polska" → "Polna 1, 16-080 Tykocin, Polska", then "16-080 Tykocin, Polska".</summary>
    internal static IEnumerable<string> Variants(string address)
    {
        var parts = address.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count == 0)
        {
            yield break;
        }

        parts[0] = StreetPrefixRegex().Replace(parts[0], string.Empty).Trim();
        yield return string.Join(", ", parts);

        if (parts.Count > 2)
        {
            yield return string.Join(", ", parts.Skip(1));
        }
    }

    private async Task<GeoPoint?> SearchAsync(string query, CancellationToken ct)
    {
        await Throttle.WaitAsync(ct);
        try
        {
            var wait = _lastCall.AddSeconds(1.1) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct);
            }

            _lastCall = DateTime.UtcNow;
            // Polish addresses are searched only in Poland (avoids same-named towns abroad); foreign ones anywhere.
            var polish = query.EndsWith("Polska", StringComparison.OrdinalIgnoreCase);
            var url = $"search?format=jsonv2&limit=1{(polish ? "&countrycodes=pl" : string.Empty)}&accept-language=pl&q={Uri.EscapeDataString(query)}";
            var place = (await http.GetFromJsonAsync<Place[]>(url, ct))?.FirstOrDefault();

            return place is not null &&
                double.TryParse(place.Lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) &&
                double.TryParse(place.Lon, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
                    ? new GeoPoint(Math.Round(lat, 6), Math.Round(lon, 6))
                    : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "Geocoding failed");
            return null;
        }
        finally
        {
            Throttle.Release();
        }
    }

    [GeneratedRegex(@"^(ul\.?|ulica|al\.?|aleja|aleje|os\.?|osiedle|pl\.?|plac)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex StreetPrefixRegex();

    private sealed record Place([property: JsonPropertyName("lat")] string? Lat, [property: JsonPropertyName("lon")] string? Lon);
}

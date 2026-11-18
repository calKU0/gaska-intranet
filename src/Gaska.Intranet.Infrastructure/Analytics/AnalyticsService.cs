using System.Security.Claims;
using Gaska.Intranet.Core.Access;
using Gaska.Intranet.Core.Analytics;
using Microsoft.Extensions.Caching.Memory;

namespace Gaska.Intranet.Infrastructure.Analytics;

/// <summary>
/// Metrics of all sources behind one permission check: a metric is visible only to users with access to its data scope
/// (admin panel → "Dostęp do danych"). Results are cached for a few minutes – dashboards of many users ask for the
/// same numbers, and the ERP / CRM should not be queried on every page view.
/// </summary>
internal sealed class AnalyticsService : IAnalyticsService
{
    private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(10);

    private readonly IModuleAccessService _access;
    private readonly IMemoryCache _cache;
    private readonly Dictionary<string, (MetricDefinition Metric, IMetricSource Source)> _metrics;

    public AnalyticsService(IEnumerable<IMetricSource> sources, IModuleAccessService access, IMemoryCache cache)
    {
        _access = access;
        _cache = cache;

        // A source registered later replaces an earlier one with the same metric (demo data instead of the real systems).
        _metrics = new Dictionary<string, (MetricDefinition, IMetricSource)>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            foreach (var metric in source.Metrics)
            {
                _metrics[metric.Key] = (metric, source);
            }
        }
    }

    public MetricDefinition? Find(string metricKey) => _metrics.TryGetValue(metricKey, out var entry) ? entry.Metric : null;

    public async Task<IReadOnlyList<MetricDefinition>> GetAvailableAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        var available = new List<MetricDefinition>();
        var scopes = new Dictionary<string, bool>(StringComparer.Ordinal);

        // Catalog order, so the list reads the same for everyone.
        foreach (var metric in MetricCatalog.All.Where(m => _metrics.ContainsKey(m.Key)))
        {
            if (!scopes.TryGetValue(metric.Scope, out var allowed))
            {
                scopes[metric.Scope] = allowed = await _access.CanAccessAsync(user, metric.Scope, ct);
            }

            if (allowed)
            {
                available.Add(metric);
            }
        }

        return available;
    }

    public async Task<bool> CanViewAsync(ClaimsPrincipal user, string metricKey, CancellationToken ct = default) =>
        Find(metricKey) is { } metric && await _access.CanAccessAsync(user, metric.Scope, ct);

    public async Task<IReadOnlyList<SeriesPoint>> GetSeriesAsync(
        ClaimsPrincipal user, string metricKey, MetricQuery query, Granularity granularity, CancellationToken ct = default)
    {
        var (metric, source) = await AuthorizeAsync(user, metricKey, MetricShape.Series, query, ct);
        var range = query.Range;

        var daily = await _cache.GetOrCreateAsync($"analytics:daily:{CacheKey(metric, query)}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTime;
            return source.GetDailyAsync(metric.Key, query, ct);
        }) ?? [];

        var totals = daily
            .Where(d => range.Contains(d.Day))
            .GroupBy(d => Periods.BucketStart(d.Day, granularity))
            .ToDictionary(g => g.Key, g => g.Sum(d => d.Value));

        return Periods.Buckets(range, granularity)
            .Select(b => new SeriesPoint(b.Start, b.Label, totals.GetValueOrDefault(b.Start)))
            .ToList();
    }

    public async Task<Ranking> GetRankingAsync(ClaimsPrincipal user, string metricKey, MetricQuery query, int top, CancellationToken ct = default)
    {
        var (metric, source) = await AuthorizeAsync(user, metricKey, MetricShape.Ranking, query, ct);
        top = Math.Clamp(top, 1, 50);

        return await _cache.GetOrCreateAsync($"analytics:ranking:{CacheKey(metric, query)}:{top}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTime;
            return source.GetRankingAsync(metric.Key, query, top, ct);
        }) ?? Ranking.Empty;
    }

    public async Task<decimal> GetSnapshotAsync(ClaimsPrincipal user, string metricKey, MetricQuery query, CancellationToken ct = default)
    {
        var (metric, source) = await AuthorizeAsync(user, metricKey, MetricShape.Snapshot, query, ct);

        return await _cache.GetOrCreateAsync($"analytics:snapshot:{CacheKey(metric, query)}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTime;
            return source.GetSnapshotAsync(metric.Key, query, ct);
        });
    }

    /// <summary>Metric + what it depends on: the contractor, and the period unless the metric is a state as of now.</summary>
    private static string CacheKey(MetricDefinition metric, MetricQuery query) =>
        $"{metric.Key}:{(metric.AsOfNow ? "now" : query.Range.ToString())}:{query.Contractor?.CacheKey ?? "all"}";

    private async Task<(MetricDefinition Metric, IMetricSource Source)> AuthorizeAsync(
        ClaimsPrincipal user, string metricKey, MetricShape shape, MetricQuery query, CancellationToken ct)
    {
        if (!_metrics.TryGetValue(metricKey, out var entry))
        {
            throw new AnalyticsException("Te dane nie są już dostępne w intranecie.");
        }

        if (!await _access.CanAccessAsync(user, entry.Metric.Scope, ct))
        {
            throw new AnalyticsException("Nie masz dostępu do tych danych.", accessDenied: true);
        }

        if (entry.Metric.Shape != shape)
        {
            throw new ArgumentException($"Metric '{metricKey}' is a {entry.Metric.Shape}, not a {shape}.", nameof(metricKey));
        }

        if (query.Contractor is not null && !entry.Metric.PerContractor)
        {
            throw new ArgumentException($"Metric '{metricKey}' cannot be narrowed to a contractor.", nameof(query));
        }

        return entry;
    }
}

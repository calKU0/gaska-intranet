using System.Security.Claims;

namespace Gaska.Intranet.Core.Analytics;

public enum MetricShape
{
    /// <summary>A value per day, grouped into days / weeks / months … on the chart.</summary>
    Series = 1,

    /// <summary>The top N names with a value (customers, products, employees).</summary>
    Ranking = 2,

    /// <summary>One number describing the state right now (open complaints, receivables) – it has no period.</summary>
    Snapshot = 3,
}

public enum MetricUnit
{
    Count = 1,

    /// <summary>Net amount in PLN.</summary>
    Money = 2,

    Days = 3,
}

/// <summary>
/// Something a dashboard chart can show. The key is stored with saved charts – do not change it after deployment.
/// </summary>
public sealed record MetricDefinition
{
    public required string Key { get; init; }

    public required string Title { get; init; }

    /// <summary>What exactly is counted – shown when picking the data and under the chart.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Where the numbers come from, in a few words ("ERP XL · wg daty sprzedaży · netto w zł") – the chart's footer.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Data scope (<see cref="DataScopes"/>) the user needs to see or add this metric.</summary>
    public required string Scope { get; init; }

    public MetricShape Shape { get; init; } = MetricShape.Series;

    public MetricUnit Unit { get; init; } = MetricUnit.Count;

    /// <summary>Icon name – the UI layer maps it to its icon set.</summary>
    public string Icon { get; init; } = "chart";

    /// <summary>The state as of today – the chart's period is ignored (every snapshot, and rankings such as open complaints).</summary>
    public bool AsOfNow { get; init; }

    /// <summary>Ranking rows come in a meaningful order (e.g. age brackets) – they are not parts to be sorted by size.</summary>
    public bool KeepOrder { get; init; }

    /// <summary>The rows add up to a whole, so a share (donut) makes sense – not for lists like "days without contact".</summary>
    public bool PartsOfWhole { get; init; } = true;

    /// <summary>The metric can be narrowed to one contractor (contractor card tabs).</summary>
    public bool PerContractor { get; init; }
}

public sealed record DailyValue(DateOnly Day, decimal Value);

/// <param name="Detail">More about the row (e.g. the product's name under its code) – shown as a hint.</param>
public sealed record RankedValue(string Label, decimal Value, string? Detail = null);

/// <param name="Total">Sum over all rows, also those below the top N – lets a donut show "the rest"; null when unknown.</param>
public sealed record Ranking(IReadOnlyList<RankedValue> Rows, decimal? Total = null)
{
    public static readonly Ranking Empty = new([]);
}

/// <summary>One bar / point of a chart: a bucket of the period with its total.</summary>
public sealed record SeriesPoint(DateOnly Start, string Label, decimal Value);

/// <summary>
/// A contractor as the data sources know it: the CRM id and the ERP cards (usually one) it was matched to. The B2B
/// platform uses the ERP card ids.
/// </summary>
public sealed record ContractorRef(int CrmId, IReadOnlyList<int> ErpIds)
{
    public bool HasErpCard => ErpIds.Count > 0;

    /// <summary>Stable text for cache keys.</summary>
    public string CacheKey => $"{CrmId}/{string.Join(',', ErpIds)}";
}

/// <summary>What a metric is asked for: the period and, on a contractor card, the contractor.</summary>
public sealed record MetricQuery(DayRange Range, ContractorRef? Contractor = null);

/// <summary>A system the metrics are read from (ERP, CRM, B2B). Sources do not check permissions – the service does.</summary>
public interface IMetricSource
{
    IReadOnlyList<MetricDefinition> Metrics { get; }

    /// <summary>Totals per day inside the range (days without data may be left out).</summary>
    Task<IReadOnlyList<DailyValue>> GetDailyAsync(string metricKey, MetricQuery query, CancellationToken ct = default);

    /// <summary>The <paramref name="top"/> largest values, largest first (or in the metric's own order).</summary>
    Task<Ranking> GetRankingAsync(string metricKey, MetricQuery query, int top, CancellationToken ct = default);

    /// <summary>The value as of now.</summary>
    Task<decimal> GetSnapshotAsync(string metricKey, MetricQuery query, CancellationToken ct = default);
}

/// <summary>The data cannot be shown – the message is safe to show to the user.</summary>
public sealed class AnalyticsException(string message, bool accessDenied = false, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The user has no permission to the metric's data scope (as opposed to a source being unavailable).</summary>
    public bool AccessDenied { get; } = accessDenied;
}

/// <summary>Metrics for dashboards and contractor cards – every read checks the user's permission to the metric's data scope.</summary>
public interface IAnalyticsService
{
    MetricDefinition? Find(string metricKey);

    /// <summary>Metrics the user may see and add to their dashboards.</summary>
    Task<IReadOnlyList<MetricDefinition>> GetAvailableAsync(ClaimsPrincipal user, CancellationToken ct = default);

    Task<bool> CanViewAsync(ClaimsPrincipal user, string metricKey, CancellationToken ct = default);

    /// <summary>The series grouped into buckets – every bucket of the range is present, empty ones as zero.</summary>
    Task<IReadOnlyList<SeriesPoint>> GetSeriesAsync(
        ClaimsPrincipal user, string metricKey, MetricQuery query, Granularity granularity, CancellationToken ct = default);

    Task<Ranking> GetRankingAsync(ClaimsPrincipal user, string metricKey, MetricQuery query, int top, CancellationToken ct = default);

    Task<decimal> GetSnapshotAsync(ClaimsPrincipal user, string metricKey, MetricQuery query, CancellationToken ct = default);
}

/// <summary>Finds the ERP cards of a CRM contractor – by the acronym, then by the tax number.</summary>
public interface IContractorLinks
{
    Task<ContractorLink> FindAsync(int crmId, string shortName, string? nip, CancellationToken ct = default);
}

/// <param name="ErpAcronyms">Acronyms of the matched ERP cards, for display.</param>
public sealed record ContractorLink(ContractorRef Contractor, IReadOnlyList<string> ErpAcronyms);

/// <summary>Turnover of one period on the contractor card.</summary>
public sealed record TurnoverPeriod(DayRange Range, decimal Net, int Documents, IReadOnlyList<MonthlyTurnover> Months);

public sealed record MonthlyTurnover(int Year, int Month, decimal Net);

/// <param name="ErpAcronyms">ERP cards the CRM contractor was matched to (usually one).</param>
public sealed record ContractorTurnover(IReadOnlyList<string> ErpAcronyms, IReadOnlyList<TurnoverPeriod> Periods);

/// <summary>
/// Contractor turnover from the ERP: net value of goods on sales invoices and receipts less their corrections, by sale date.
/// </summary>
public interface IContractorTurnoverService
{
    /// <summary>The most periods that can be compared at once.</summary>
    const int MaxPeriods = 5;

    /// <summary>
    /// Turnover of the contractor in each period. Null when the contractor has no card in the ERP. Requires the
    /// <see cref="DataScopes.Sales"/> permission.
    /// </summary>
    Task<ContractorTurnover?> GetAsync(
        ClaimsPrincipal user, ContractorLink contractor, IReadOnlyList<DayRange> periods, CancellationToken ct = default);
}

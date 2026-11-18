using Gaska.Intranet.Core.Analytics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Gaska.Intranet.Infrastructure.Analytics;

/// <summary>
/// Customer activity on the B2B platform – its report tables (<c>ldd.Rpt*</c>: logins, searches, page views, cart).
/// Read through <c>ConnectionStrings:B2B</c>. Customers are identified by their ERP card id (<c>…_KntID</c>).
/// Orders placed on the platform are counted in the ERP (see <see cref="ErpMetricSource"/>).
/// </summary>
internal sealed class B2BMetricSource(IConfiguration configuration, ILogger<B2BMetricSource> logger) : IMetricSource
{
    public const string ConnectionStringName = "B2B";

    private readonly SqlSource _b2b = new(configuration, "B2B", logger, ConnectionStringName);

    public IReadOnlyList<MetricDefinition> Metrics { get; } = MetricCatalog.B2B.Where(m => m.Key != MetricCatalog.B2BOrders).ToList();

    public async Task<IReadOnlyList<DailyValue>> GetDailyAsync(string metricKey, MetricQuery query, CancellationToken ct = default)
    {
        if (HasNoCard(query))
        {
            return [];
        }

        return metricKey switch
        {
            MetricCatalog.B2BLogins => await DailyAsync("ldd.RptLogowanie", "R_LG", "R_LG_Sukces = 1", query, ct),
            MetricCatalog.B2BSearches => await DailyAsync("ldd.RptWyszukiwanie", "R_SRC", null, query, ct),
            MetricCatalog.B2BEmptySearches => await DailyAsync("ldd.RptWyszukiwanie", "R_SRC", "R_SRC_Ilosc = 0", query, ct),
            MetricCatalog.B2BPageViews => await DailyAsync("ldd.RptOdwiedzoneStrony", "R_STR", null, query, ct),
            MetricCatalog.B2BCartRemovals => await DailyAsync("ldd.RptUsuniecieZKoszyka", "R_UZK", null, query, ct),
            _ => throw new ArgumentException($"Unknown B2B series '{metricKey}'.", nameof(metricKey)),
        };
    }

    public async Task<Ranking> GetRankingAsync(string metricKey, MetricQuery query, int top, CancellationToken ct = default)
    {
        if (HasNoCard(query))
        {
            return Ranking.Empty;
        }

        const string Phrase = "UPPER(LTRIM(RTRIM(R_SRC_Zapytanie)))";

        // Product page views carry "id:code" – the code is what people recognise.
        const string ProductCode = "SUBSTRING(R_STR_Dane, CHARINDEX(':', R_STR_Dane) + 1, 200)";

        return metricKey switch
        {
            MetricCatalog.B2BTopSearches => await RankingAsync(Phrase, "ldd.RptWyszukiwanie", "R_SRC", $"{Phrase} <> ''", query, top, ct),
            MetricCatalog.B2BTopEmptySearches => await RankingAsync(Phrase, "ldd.RptWyszukiwanie", "R_SRC", $"R_SRC_Ilosc = 0 AND {Phrase} <> ''", query, top, ct),
            MetricCatalog.B2BTopProducts => await RankingAsync(ProductCode, "ldd.RptOdwiedzoneStrony", "R_STR",
                "R_STR_Nazwa = N'Produkt' AND CHARINDEX(':', R_STR_Dane) > 0", query, top, ct),
            MetricCatalog.B2BTopCustomers => await RankingAsync("R_LG_Akronim", "ldd.RptLogowanie", "R_LG", "R_LG_Sukces = 1 AND R_LG_Akronim <> ''", query, top, ct),
            _ => throw new ArgumentException($"Unknown B2B ranking '{metricKey}'.", nameof(metricKey)),
        };
    }

    public Task<decimal> GetSnapshotAsync(string metricKey, MetricQuery query, CancellationToken ct = default) =>
        throw new ArgumentException($"Unknown B2B snapshot '{metricKey}'.", nameof(metricKey));

    private static bool HasNoCard(MetricQuery query) => query.Contractor is { HasErpCard: false };

    /// <summary>
    /// The period and, on a contractor card, the customer. Every report table names its columns with one prefix:
    /// <c>{prefix}_Data</c>, <c>{prefix}_KntID</c>. Table, prefix and filter are constants from this class.
    /// </summary>
    private static string Where(string prefix, string? filter, MetricQuery query) =>
        $"{prefix}_Data >= @from AND {prefix}_Data < @to" +
        (filter is null ? "" : $" AND {filter}") +
        (query.Contractor is { } contractor ? $" AND {prefix}_KntID IN ({SqlSource.Ids(contractor.ErpIds)})" : "");

    private async Task<IReadOnlyList<DailyValue>> DailyAsync(string table, string prefix, string? filter, MetricQuery query, CancellationToken ct) =>
        await _b2b.QueryAsync($"""
            SELECT CAST({prefix}_Data AS date), COUNT(*)
            FROM {table}
            WHERE {Where(prefix, filter, query)}
            GROUP BY CAST({prefix}_Data AS date)
            """,
            r => new DailyValue(DateOnly.FromDateTime(r.GetDateTime(0)), SqlSource.Number(r, 1)), ct, Range(query));

    private Task<Ranking> RankingAsync(string label, string table, string prefix, string filter, MetricQuery query, int top, CancellationToken ct) =>
        _b2b.RankingAsync($"""
            SELECT {label} AS label, NULL AS detail, COUNT(*) AS v
            FROM {table}
            WHERE {Where(prefix, filter, query)}
            GROUP BY {label}
            """, top, ct, Range(query));

    private static (string, object)[] Range(MetricQuery query) =>
    [
        ("@from", query.Range.From.ToDateTime(TimeOnly.MinValue)),
        ("@to", query.Range.To.AddDays(1).ToDateTime(TimeOnly.MinValue)),
    ];
}

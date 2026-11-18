using System.Security.Claims;
using Gaska.Intranet.Core.Access;
using Gaska.Intranet.Core.Analytics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Infrastructure.Analytics;

/// <summary>
/// Comarch ERP XL settings ("Erp" section). The database is read through <c>ConnectionStrings:Erp</c>, or – when the
/// application's own tables live in the ERP database – through <c>ConnectionStrings:Intranet</c>.
/// </summary>
public sealed class ErpOptions
{
    public const string SectionName = "Erp";

    /// <summary>Connection strings tried in order.</summary>
    public static readonly string[] ConnectionStrings = ["Erp", "Intranet"];

    /// <summary>Document series of quality complaints (CDN.ReklNag.RLN_Seria) – specific to the company's ERP setup.</summary>
    public string[] QualityComplaintSeries { get; set; } = [];

    /// <summary>Document series of delivery complaints.</summary>
    public string[] DeliveryComplaintSeries { get; set; } = [];

    /// <summary>Document series of returns.</summary>
    public string[] ReturnSeries { get; set; } = [];

    /// <summary>Identifiers of the ERP operators that issue the sales orders placed on the B2B platform (CDN.OpeKarty.Ope_Ident).</summary>
    public string[] B2BOrderOperators { get; set; } = [];
}

/// <summary>What counts as sales in the ERP – shared by the dashboards and the contractor's turnover.</summary>
internal static class ErpSales
{
    /// <summary>
    /// Confirmed sales invoices (FS 2033, export FSE 2037), receipts (PA 2034) and their corrections (FSK 2041,
    /// FKE 2045, PAK 2042). <c>TrN_Data3</c> is the sale date.
    /// </summary>
    public const string Documents = "n.TrN_GIDTyp IN (2033, 2037, 2034, 2041, 2045, 2042) AND n.TrN_Stan IN (3, 4, 5)";

    /// <summary>Sales documents without corrections – for counting invoices.</summary>
    public const string IsInvoice = "n.TrN_GIDTyp IN (2033, 2037, 2034)";

    /// <summary>Lines of documents (<c>e</c>) with their headers (<c>n</c>).</summary>
    public const string Lines = """
        FROM CDN.TraElem e
        JOIN CDN.TraNag n ON n.TrN_GIDTyp = e.TrE_GIDTyp AND n.TrN_GIDNumer = e.TrE_GIDNumer
        """;

    /// <summary>
    /// Sold lines: lines of the sales documents – goods, products, services and costs alike (transport, handling, …), so
    /// turnover matches the invoices. An invoice that only binds goods issues has no lines of its own – they are on the
    /// issues (WZ 2001, WZE 2005 and their corrections 2009, 2013), so bound issues are read instead; nothing is counted
    /// twice. <c>TrE_KsiegowaNetto</c> is the net value in PLN.
    /// </summary>
    public const string SoldLines = """
        n.TrN_Stan IN (3, 4, 5)
        AND (n.TrN_GIDTyp IN (2033, 2037, 2034, 2041, 2045, 2042) OR (n.TrN_GIDTyp IN (2001, 2005, 2009, 2013) AND n.TrN_SpiNumer <> 0))
        """;

    /// <summary>Only goods and products (<c>TrE_TypTwr</c> 1, 2) – for rankings of products, where services and costs are noise.</summary>
    public const string GoodsOnly = "e.TrE_TypTwr IN (1, 2)";
}

/// <summary>Sales, complaints, returns, receivables and B2B orders from Comarch ERP XL (read-only).</summary>
internal sealed class ErpMetricSource(
    IConfiguration configuration, IOptions<ErpOptions> options, TimeProvider clock, ILogger<ErpMetricSource> logger) : IMetricSource
{
    /// <summary>Complaint documents waiting for a decision: unconfirmed (1), confirmed (10), being handled (20). Cancelled = 6.</summary>
    private const string OpenComplaint = "r.RLN_Stan IN (1, 10, 20)";

    /// <summary>Unsettled payments of sales documents; a receivable (type 2) counts up, an amount to return to the customer down.</summary>
    private const string Receivables = """
        FROM CDN.TraPlat p
        JOIN CDN.KntKarty k ON k.Knt_GIDNumer = p.TrP_KntNumer
        """;

    private const string ReceivablesFilter = "p.TrP_GIDTyp IN (2033, 2037, 2034, 2041, 2045, 2042) AND p.TrP_Rozliczona = 0 AND p.TrP_KntTyp = 32";
    private const string Amount = "CASE WHEN p.TrP_Typ = 2 THEN p.TrP_PozostajeSys ELSE -p.TrP_PozostajeSys END";

    private readonly SqlSource _erp = new(configuration, "ERP XL", logger, ErpOptions.ConnectionStrings);
    private readonly ErpOptions _options = options.Value;

    public IReadOnlyList<MetricDefinition> Metrics { get; } =
    [
        .. MetricCatalog.Sales, .. MetricCatalog.Complaints, .. MetricCatalog.Receivables,
        .. MetricCatalog.B2B.Where(m => m.Key == MetricCatalog.B2BOrders),
    ];

    private string[] ComplaintSeries => [.. _options.QualityComplaintSeries, .. _options.DeliveryComplaintSeries];

    private string[] AllSeries => [.. ComplaintSeries, .. _options.ReturnSeries];

    public async Task<IReadOnlyList<DailyValue>> GetDailyAsync(string metricKey, MetricQuery query, CancellationToken ct = default)
    {
        if (HasNoCard(query))
        {
            return [];
        }

        switch (metricKey)
        {
            case MetricCatalog.SalesNet:
                return await DailyAsync($"""
                    SELECT n.TrN_Data3, SUM(e.TrE_KsiegowaNetto)
                    {ErpSales.Lines}
                    WHERE {ErpSales.SoldLines} AND n.TrN_Data3 BETWEEN @from AND @to{Contractor("n.TrN_KntNumer", query)}
                    GROUP BY n.TrN_Data3
                    """, query, ct);
            case MetricCatalog.SalesDocuments:
                return await DailyAsync($"""
                    SELECT n.TrN_Data3, COUNT(*)
                    FROM CDN.TraNag n
                    WHERE {ErpSales.Documents} AND {ErpSales.IsInvoice} AND n.TrN_Data3 BETWEEN @from AND @to{Contractor("n.TrN_KntNumer", query)}
                    GROUP BY n.TrN_Data3
                    """, query, ct);
            case MetricCatalog.SalesOrders:
                // Sales orders (ZS = 1280) that were not cancelled.
                return await DailyAsync($"""
                    SELECT z.ZaN_DataWystawienia, COUNT(*)
                    FROM CDN.ZamNag z
                    WHERE z.ZaN_ZamTyp = 1280 AND z.ZaN_Stan NOT IN (1, 19, 35) AND z.ZaN_DataWystawienia BETWEEN @from AND @to{Contractor("z.ZaN_KntNumer", query)}
                    GROUP BY z.ZaN_DataWystawienia
                    """, query, ct);
            case MetricCatalog.B2BOrders:
            {
                var (operators, parameters) = Configured("o", _options.B2BOrderOperators, "Erp:B2BOrderOperators");
                return await DailyAsync($"""
                    SELECT z.ZaN_DataWystawienia, COUNT(*)
                    FROM CDN.ZamNag z
                    WHERE z.ZaN_ZamTyp = 1280 AND z.ZaN_Stan NOT IN (1, 19, 35) AND z.ZaN_DataWystawienia BETWEEN @from AND @to
                      AND z.ZaN_OpeNumerW IN (SELECT Ope_GIDNumer FROM CDN.OpeKarty WHERE Ope_Ident IN ({operators})){Contractor("z.ZaN_KntNumer", query)}
                    GROUP BY z.ZaN_DataWystawienia
                    """, query, ct, parameters);
            }

            case MetricCatalog.ComplaintsQuality:
                return await ComplaintsAsync(_options.QualityComplaintSeries, "Erp:QualityComplaintSeries", query, ct);
            case MetricCatalog.ComplaintsDelivery:
                return await ComplaintsAsync(_options.DeliveryComplaintSeries, "Erp:DeliveryComplaintSeries", query, ct);
            case MetricCatalog.ComplaintsReturns:
                return await ComplaintsAsync(_options.ReturnSeries, "Erp:ReturnSeries", query, ct);
            default:
                throw new ArgumentException($"Unknown ERP series '{metricKey}'.", nameof(metricKey));
        }
    }

    public async Task<Ranking> GetRankingAsync(string metricKey, MetricQuery query, int top, CancellationToken ct = default)
    {
        if (HasNoCard(query))
        {
            return Ranking.Empty;
        }

        var salesKnt = Contractor("n.TrN_KntNumer", query);
        switch (metricKey)
        {
            case MetricCatalog.SalesTopCustomers:
                return await _erp.RankingAsync($"""
                    SELECT k.Knt_Akronim AS label, MAX(k.Knt_Nazwa1) AS detail, SUM(e.TrE_KsiegowaNetto) AS v
                    {ErpSales.Lines}
                    JOIN CDN.KntKarty k ON k.Knt_GIDNumer = n.TrN_KntNumer
                    WHERE {ErpSales.SoldLines} AND n.TrN_KntTyp = 32 AND n.TrN_Data3 BETWEEN @from AND @to
                    GROUP BY k.Knt_Akronim
                    """, top, ct, Range(query));
            case MetricCatalog.SalesByCountry:
                return await _erp.RankingAsync($"""
                    SELECT CASE WHEN k.Knt_Kraj = '' THEN '(brak)' ELSE k.Knt_Kraj END AS label, NULL AS detail, SUM(e.TrE_KsiegowaNetto) AS v
                    {ErpSales.Lines}
                    JOIN CDN.KntKarty k ON k.Knt_GIDNumer = n.TrN_KntNumer
                    WHERE {ErpSales.SoldLines} AND n.TrN_KntTyp = 32 AND n.TrN_Data3 BETWEEN @from AND @to
                    GROUP BY k.Knt_Kraj
                    """, top, ct, Range(query));
            case MetricCatalog.SalesBySalesperson:
                // The salesperson of a document is its "akwizytor" – an employee card (type 944).
                return await _erp.RankingAsync($"""
                    SELECT LTRIM(RTRIM(MAX(s.Prc_Imie1) + ' ' + MAX(s.Prc_Nazwisko))) AS label, NULL AS detail, SUM(e.TrE_KsiegowaNetto) AS v
                    {ErpSales.Lines}
                    JOIN CDN.PrcKarty s ON s.Prc_GIDNumer = n.TrN_AkwNumer
                    WHERE {ErpSales.SoldLines} AND n.TrN_AkwTyp = 944 AND n.TrN_Data3 BETWEEN @from AND @to
                    GROUP BY s.Prc_GIDNumer
                    """, top, ct, Range(query));
            case MetricCatalog.SalesTopProducts:
                return await _erp.RankingAsync($"""
                    SELECT MAX(e.TrE_TwrKod) AS label, MAX(e.TrE_TwrNazwa) AS detail, SUM(e.TrE_KsiegowaNetto) AS v
                    {ErpSales.Lines}
                    WHERE {ErpSales.SoldLines} AND {ErpSales.GoodsOnly} AND n.TrN_Data3 BETWEEN @from AND @to{salesKnt}
                    GROUP BY e.TrE_TwrNumer
                    """, top, ct, Range(query));

            case MetricCatalog.ComplaintsTopProducts:
                return await ComplaintLinesAsync("MAX(e.RLE_TwrKod)", "MAX(e.RLE_TwrNazwa)", "e.RLE_TwrNumer", "e.RLE_TypTwr IN (1, 2)",
                    _options.QualityComplaintSeries, "Erp:QualityComplaintSeries", query, top, ct);
            case MetricCatalog.ComplaintsTopReturnedProducts:
                return await ComplaintLinesAsync("MAX(e.RLE_TwrKod)", "MAX(e.RLE_TwrNazwa)", "e.RLE_TwrNumer", "e.RLE_TypTwr IN (1, 2)",
                    _options.ReturnSeries, "Erp:ReturnSeries", query, top, ct);
            case MetricCatalog.ComplaintsTopReasons:
            {
                // Free text – the same reason written with different case, spaces or a full stop is one reason.
                const string Reason = "UPPER(LTRIM(RTRIM(REPLACE(e.RLE_Przyczyna, '.', ''))))";
                return await ComplaintLinesAsync(Reason, "NULL", Reason, $"{Reason} <> ''",
                    _options.QualityComplaintSeries, "Erp:QualityComplaintSeries", query, top, ct);
            }

            case MetricCatalog.ComplaintsRequests:
                return await ComplaintLinesAsync("MAX(d.SLW_WartoscS)", "NULL", "e.RLE_Zadanie", "1 = 1",
                    AllSeries, "Erp:QualityComplaintSeries / DeliveryComplaintSeries / ReturnSeries", query, top, ct,
                    join: "JOIN CDN.Slowniki d ON d.SLW_ID = e.RLE_Zadanie");
            case MetricCatalog.ComplaintsTopCustomers:
            {
                var (series, parameters) = Configured("s", AllSeries, "Erp:QualityComplaintSeries / DeliveryComplaintSeries / ReturnSeries");
                return await _erp.RankingAsync($"""
                    SELECT k.Knt_Akronim AS label, MAX(k.Knt_Nazwa1) AS detail, COUNT(*) AS v
                    FROM CDN.ReklNag r
                    JOIN CDN.KntKarty k ON k.Knt_GIDNumer = r.RLN_KntNumer
                    WHERE r.RLN_Stan <> 6 AND r.RLN_Seria IN ({series}) AND r.RLN_DataWyst BETWEEN @from AND @to
                    GROUP BY k.Knt_Akronim
                    """, top, ct, [.. Range(query), .. parameters]);
            }

            case MetricCatalog.ComplaintsOpenList:
            {
                // Oldest first – a list of documents, not shares of a whole.
                var (series, parameters) = Configured("s", AllSeries, "Erp:QualityComplaintSeries / DeliveryComplaintSeries / ReturnSeries");
                var rows = await _erp.QueryAsync($"""
                    SELECT TOP (@top)
                        CONCAT(r.RLN_Numer, '/', RIGHT(r.RLN_Rok, 2), '/', r.RLN_Seria, ' · ', k.Knt_Akronim),
                        @today - r.RLN_DataWyst,
                        r.RLN_DataWyst
                    FROM CDN.ReklNag r
                    JOIN CDN.KntKarty k ON k.Knt_GIDNumer = r.RLN_KntNumer
                    WHERE {OpenComplaint} AND r.RLN_Seria IN ({series}){Contractor("r.RLN_KntNumer", query)}
                    ORDER BY r.RLN_DataWyst, r.RLN_Id
                    """,
                    r => new RankedValue(SqlSource.Text(r, 0), SqlSource.Number(r, 1), $"wystawiona {ClarionDate.ToDate(r.GetInt32(2)):yyyy-MM-dd}"),
                    ct, [("@top", top), ("@today", Today), .. parameters]);
                return new Ranking(rows);
            }

            case MetricCatalog.ReceivablesAging:
            {
                // Fixed brackets in their natural order; empty brackets are left out by the query and stay out.
                var rows = await _erp.QueryAsync($"""
                    SELECT b.Bracket, SUM(b.Amount)
                    FROM (
                        SELECT {Amount} AS Amount,
                               CASE WHEN p.TrP_Termin >= @today THEN 0
                                    WHEN @today - p.TrP_Termin <= 30 THEN 1
                                    WHEN @today - p.TrP_Termin <= 60 THEN 2
                                    WHEN @today - p.TrP_Termin <= 90 THEN 3
                                    ELSE 4 END AS Bracket
                        {Receivables}
                        WHERE {ReceivablesFilter}{Contractor("p.TrP_KntNumer", query)}
                    ) b
                    GROUP BY b.Bracket
                    ORDER BY b.Bracket
                    """,
                    r => new RankedValue(AgeBracket(r.GetInt32(0)), SqlSource.Number(r, 1)), ct, ("@today", Today));
                return new Ranking(rows, rows.Sum(r => r.Value));
            }

            case MetricCatalog.ReceivablesTopDebtors:
                return await _erp.RankingAsync($"""
                    SELECT k.Knt_Akronim AS label, MAX(k.Knt_Nazwa1) AS detail, SUM({Amount}) AS v
                    {Receivables}
                    WHERE {ReceivablesFilter} AND p.TrP_Termin < @today
                    GROUP BY k.Knt_Akronim
                    HAVING SUM({Amount}) > 0
                    """, top, ct, ("@today", Today));
            case MetricCatalog.ReceivablesOverdueInvoices:
            {
                var rows = await _erp.QueryAsync($"""
                    SELECT TOP (@top)
                        CONCAT(CASE n.TrN_GIDTyp WHEN 2033 THEN 'FS' WHEN 2037 THEN 'FSE' WHEN 2034 THEN 'PA' ELSE 'FS' END, '-',
                               n.TrN_TrNNumer, '/', RIGHT(n.TrN_TrNRok, 2), CASE WHEN n.TrN_TrNSeria = '' THEN '' ELSE '/' + n.TrN_TrNSeria END,
                               ' · ', k.Knt_Akronim, ' · ', @today - p.TrP_Termin, ' dni po terminie'),
                        p.TrP_PozostajeSys,
                        p.TrP_Termin
                    {Receivables}
                    JOIN CDN.TraNag n ON n.TrN_GIDTyp = p.TrP_GIDTyp AND n.TrN_GIDNumer = p.TrP_GIDNumer
                    WHERE {ReceivablesFilter} AND p.TrP_Typ = 2 AND p.TrP_GIDTyp IN (2033, 2037, 2034) AND p.TrP_Termin < @today
                      AND p.TrP_PozostajeSys > 0{Contractor("p.TrP_KntNumer", query)}
                    ORDER BY p.TrP_PozostajeSys DESC
                    """,
                    r => new RankedValue(SqlSource.Text(r, 0), SqlSource.Number(r, 1), $"termin płatności {ClarionDate.ToDate(r.GetInt32(2)):yyyy-MM-dd}"),
                    ct, ("@top", top), ("@today", Today));
                return new Ranking(rows);
            }

            default:
                throw new ArgumentException($"Unknown ERP ranking '{metricKey}'.", nameof(metricKey));
        }
    }

    public async Task<decimal> GetSnapshotAsync(string metricKey, MetricQuery query, CancellationToken ct = default)
    {
        if (HasNoCard(query))
        {
            return 0;
        }

        switch (metricKey)
        {
            case MetricCatalog.ComplaintsOpen:
                return await OpenComplaintsAsync(ComplaintSeries, "Erp:QualityComplaintSeries / DeliveryComplaintSeries", query, ct);
            case MetricCatalog.ComplaintsOpenReturns:
                return await OpenComplaintsAsync(_options.ReturnSeries, "Erp:ReturnSeries", query, ct);
            case MetricCatalog.ReceivablesTotal:
                return await ScalarAsync($"""
                    SELECT SUM({Amount}) {Receivables} WHERE {ReceivablesFilter}{Contractor("p.TrP_KntNumer", query)}
                    """, ct);
            case MetricCatalog.ReceivablesOverdue:
                return await ScalarAsync($"""
                    SELECT SUM({Amount}) {Receivables} WHERE {ReceivablesFilter} AND p.TrP_Termin < @today{Contractor("p.TrP_KntNumer", query)}
                    """, ct, ("@today", Today));
            default:
                throw new ArgumentException($"Unknown ERP snapshot '{metricKey}'.", nameof(metricKey));
        }
    }

    private int Today => ClarionDate.From(DateOnly.FromDateTime(clock.GetLocalNow().DateTime));

    private static string AgeBracket(int bracket) => bracket switch
    {
        0 => "W terminie",
        1 => "1–30 dni po terminie",
        2 => "31–60 dni po terminie",
        3 => "61–90 dni po terminie",
        _ => "Ponad 90 dni po terminie",
    };

    /// <summary>A contractor card asks for a contractor that has no card in the ERP – there is nothing to read.</summary>
    private static bool HasNoCard(MetricQuery query) => query.Contractor is { HasErpCard: false };

    /// <summary>" AND column IN (ids)" for a contractor card, empty for the whole company.</summary>
    private static string Contractor(string column, MetricQuery query) =>
        query.Contractor is { } contractor ? $" AND {column} IN ({SqlSource.Ids(contractor.ErpIds)})" : string.Empty;

    private static (string, object)[] Range(MetricQuery query) =>
        [("@from", ClarionDate.From(query.Range.From)), ("@to", ClarionDate.From(query.Range.To))];

    /// <summary>An IN list of values from configuration; a missing setting is reported in words the user can pass on.</summary>
    private static (string List, (string, object)[] Parameters) Configured(string prefix, string[] values, string setting)
    {
        if (values.Length == 0)
        {
            throw new AnalyticsException($"Te dane wymagają ustawienia {setting} w konfiguracji. Skontaktuj się z działem IT.");
        }

        return SqlSource.TextList(prefix, values);
    }

    /// <summary>Complaint documents (not cancelled) of the given series, by issue date.</summary>
    private Task<IReadOnlyList<DailyValue>> ComplaintsAsync(string[] series, string setting, MetricQuery query, CancellationToken ct)
    {
        var (list, parameters) = Configured("s", series, setting);
        return DailyAsync($"""
            SELECT r.RLN_DataWyst, COUNT(*)
            FROM CDN.ReklNag r
            WHERE r.RLN_Stan <> 6 AND r.RLN_Seria IN ({list}) AND r.RLN_DataWyst BETWEEN @from AND @to{Contractor("r.RLN_KntNumer", query)}
            GROUP BY r.RLN_DataWyst
            """, query, ct, parameters);
    }

    /// <summary>Counts lines of complaint documents grouped by something on the line (product, reason, request).</summary>
    private Task<Ranking> ComplaintLinesAsync(
        string label, string detail, string groupBy, string filter, string[] series, string setting, MetricQuery query, int top,
        CancellationToken ct, string join = "")
    {
        var (list, parameters) = Configured("s", series, setting);
        return _erp.RankingAsync($"""
            SELECT {label} AS label, {detail} AS detail, COUNT(*) AS v
            FROM CDN.ReklElem e
            JOIN CDN.ReklNag r ON r.RLN_Id = e.RLE_RLNId
            {join}
            WHERE r.RLN_Stan <> 6 AND r.RLN_Seria IN ({list}) AND r.RLN_DataWyst BETWEEN @from AND @to AND {filter}{Contractor("r.RLN_KntNumer", query)}
            GROUP BY {groupBy}
            """, top, ct, [.. Range(query), .. parameters]);
    }

    private Task<decimal> OpenComplaintsAsync(string[] series, string setting, MetricQuery query, CancellationToken ct)
    {
        var (list, parameters) = Configured("s", series, setting);
        return ScalarAsync($"""
            SELECT COUNT(*) FROM CDN.ReklNag r WHERE {OpenComplaint} AND r.RLN_Seria IN ({list}){Contractor("r.RLN_KntNumer", query)}
            """, ct, parameters);
    }

    /// <summary>Runs a query returning (Clarion date, value) rows for the range (@from, @to).</summary>
    private async Task<IReadOnlyList<DailyValue>> DailyAsync(string sql, MetricQuery query, CancellationToken ct, params (string, object)[] extra) =>
        await _erp.QueryAsync(sql, r => new DailyValue(ClarionDate.ToDate(r.GetInt32(0)), SqlSource.Number(r, 1)), ct, [.. Range(query), .. extra]);

    private async Task<decimal> ScalarAsync(string sql, CancellationToken ct, params (string, object)[] parameters) =>
        (await _erp.QueryAsync(sql, r => SqlSource.Number(r, 0), ct, parameters)).FirstOrDefault();
}

/// <summary>Matches CRM contractors with their ERP cards – by the acronym, then by the tax number.</summary>
internal sealed class ErpContractorLinks(IConfiguration configuration, IMemoryCache cache, ILogger<ErpContractorLinks> logger) : IContractorLinks
{
    private readonly SqlSource _erp = new(configuration, "ERP XL", logger, ErpOptions.ConnectionStrings);

    public async Task<ContractorLink> FindAsync(int crmId, string shortName, string? nip, CancellationToken ct = default)
    {
        var cacheKey = $"erp:cards:{crmId}:{shortName}|{nip}";
        if (cache.TryGetValue(cacheKey, out ContractorLink? cached) && cached is not null)
        {
            return cached;
        }

        static (int Id, string Acronym) Card(SqlDataReader r) => (r.GetInt32(0), SqlSource.Text(r, 1));

        var cards = await _erp.QueryAsync(
            "SELECT Knt_GIDNumer, Knt_Akronim FROM CDN.KntKarty WHERE Knt_Akronim = @acronym", Card, ct, ("@acronym", shortName.Trim()));

        // The acronym was changed on one side – the tax number still identifies the company (it may have several cards).
        var taxNumber = new string((nip ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
        if (cards.Count == 0 && taxNumber.Count(char.IsDigit) >= 8)
        {
            cards = await _erp.QueryAsync("""
                SELECT TOP 20 Knt_GIDNumer, Knt_Akronim
                FROM CDN.KntKarty
                WHERE REPLACE(REPLACE(Knt_Nip, '-', ''), ' ', '') = @nip
                ORDER BY Knt_Akronim
                """, Card, ct, ("@nip", taxNumber));
        }

        var link = new ContractorLink(new ContractorRef(crmId, cards.Select(c => c.Id).ToList()), cards.Select(c => c.Acronym).ToList());
        cache.Set(cacheKey, link, TimeSpan.FromMinutes(10));
        return link;
    }
}

/// <summary>Contractor turnover from the ERP – the same definition of sales as the dashboards.</summary>
internal sealed class ErpContractorTurnoverService(
    IConfiguration configuration, IModuleAccessService access, IMemoryCache cache, ILogger<ErpContractorTurnoverService> logger) : IContractorTurnoverService
{
    private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(5);
    private readonly SqlSource _erp = new(configuration, "ERP XL", logger, ErpOptions.ConnectionStrings);

    public async Task<ContractorTurnover?> GetAsync(
        ClaimsPrincipal user, ContractorLink contractor, IReadOnlyList<DayRange> periods, CancellationToken ct = default)
    {
        if (!await access.CanAccessAsync(user, DataScopes.Sales, ct))
        {
            throw new AnalyticsException("Nie masz dostępu do danych o sprzedaży.", accessDenied: true);
        }

        if (periods.Count > IContractorTurnoverService.MaxPeriods)
        {
            throw new ArgumentException($"At most {IContractorTurnoverService.MaxPeriods} periods can be compared.", nameof(periods));
        }

        if (!contractor.Contractor.HasErpCard)
        {
            return null;
        }

        var result = new List<TurnoverPeriod>(periods.Count);
        foreach (var period in periods)
        {
            result.Add(await GetPeriodAsync(contractor.Contractor, period, ct));
        }

        return new ContractorTurnover(contractor.ErpAcronyms, result);
    }

    private async Task<TurnoverPeriod> GetPeriodAsync(ContractorRef contractor, DayRange period, CancellationToken ct)
    {
        var cacheKey = $"turnover:{contractor.CacheKey}:{period}";
        if (cache.TryGetValue(cacheKey, out TurnoverPeriod? cached) && cached is not null)
        {
            return cached;
        }

        // Card ids are integers read from the ERP – safe to inline.
        var ids = SqlSource.Ids(contractor.ErpIds);
        (string, object)[] range = [("@from", ClarionDate.From(period.From)), ("@to", ClarionDate.From(period.To))];

        var months = await _erp.QueryAsync($"""
            SELECT YEAR(d.Day), MONTH(d.Day), SUM(e.TrE_KsiegowaNetto)
            {ErpSales.Lines}
            CROSS APPLY (SELECT DATEADD(day, n.TrN_Data3, '18001228') AS Day) d
            WHERE {ErpSales.SoldLines} AND n.TrN_KntTyp = 32 AND n.TrN_KntNumer IN ({ids}) AND n.TrN_Data3 BETWEEN @from AND @to
            GROUP BY YEAR(d.Day), MONTH(d.Day)
            ORDER BY 1, 2
            """,
            r => new MonthlyTurnover(r.GetInt32(0), r.GetInt32(1), SqlSource.Number(r, 2)), ct, range);

        var documents = await _erp.QueryAsync($"""
            SELECT COUNT(*)
            FROM CDN.TraNag n
            WHERE {ErpSales.Documents} AND {ErpSales.IsInvoice} AND n.TrN_KntTyp = 32 AND n.TrN_KntNumer IN ({ids}) AND n.TrN_Data3 BETWEEN @from AND @to
            """,
            r => (int)SqlSource.Number(r, 0), ct, range);

        var result = new TurnoverPeriod(period, months.Sum(m => m.Net), documents.FirstOrDefault(), months);
        cache.Set(cacheKey, result, CacheTime);
        return result;
    }
}

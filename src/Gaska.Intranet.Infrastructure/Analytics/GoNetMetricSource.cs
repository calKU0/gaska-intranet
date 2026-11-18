using FirebirdSql.Data.FirebirdClient;
using Gaska.Intranet.Core.Analytics;
using Gaska.Intranet.Core.Crm;
using Gaska.Intranet.Infrastructure.GoNet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Infrastructure.Analytics;

/// <summary>
/// Customer communication from the GoNet CRM: correspondence entries of contractors (KORESPONDENCJADZIENNIK – one row
/// per contractor a message / call / meeting concerns), tasks and new contractors.
/// </summary>
internal sealed class GoNetMetricSource(GoNetDatabase db, IOptions<GoNetOptions> options, ILogger<GoNetMetricSource> logger) : IMetricSource
{
    /// <summary>Correspondence entries (<c>d</c>) with the message they belong to (<c>k</c>).</summary>
    private const string Correspondence = """
        FROM KORESPONDENCJADZIENNIK d
        JOIN KORESPONDENCJA k ON k.ID = d.IDKORESPONDENCJI
        """;

    /// <summary>Entries that belong to a contractor and were not deleted, inside the period.</summary>
    private const string InPeriod = "d.USUNIETY = 0 AND k.USUNIETY = 0 AND d.IDKONTRAHENTA > 0 AND d.DATAAKCJI >= @od AND d.DATAAKCJI < @do";

    /// <summary>KORESPONDENCJADZIENNIK.IDTYPU: 1 = received, 0 = sent (GoNet's own values).</summary>
    private const string Incoming = "d.IDTYPU = 1";
    private const string Outgoing = "d.IDTYPU = 0";

    /// <summary>Open tasks past their deadline (GoNet keeps "no date" as 1899, hence the lower bound).</summary>
    private const string OverdueTask = """
        z.USUNIETY = 0 AND z.STATUS <> 2 AND z.UKONCZONO < 100
        AND z.TERMINZAKONCZENIA < CURRENT_TIMESTAMP AND z.TERMINZAKONCZENIA > '2000-01-01'
        """;

    private readonly GoNetOptions _options = options.Value;

    public IReadOnlyList<MetricDefinition> Metrics { get; } = MetricCatalog.Crm;

    public Task<IReadOnlyList<DailyValue>> GetDailyAsync(string metricKey, MetricQuery query, CancellationToken ct = default) => metricKey switch
    {
        MetricCatalog.CrmCorrespondence => CorrespondenceAsync(null, null, query, ct),
        MetricCatalog.CrmEmails => CorrespondenceAsync(EmailType, null, query, ct),
        MetricCatalog.CrmEmailsIn => CorrespondenceAsync(EmailType, Incoming, query, ct),
        MetricCatalog.CrmEmailsOut => CorrespondenceAsync(EmailType, Outgoing, query, ct),
        MetricCatalog.CrmCalls => CorrespondenceAsync(
            ActionType(_options.PhoneCorrespondenceActionType, nameof(GoNetOptions.PhoneCorrespondenceActionType)), null, query, ct),
        MetricCatalog.CrmMeetings => CorrespondenceAsync(_options.VisitCorrespondenceActionType, null, query, ct),
        MetricCatalog.CrmTasks => DailyAsync("z.TERMINROZPOCZECIA",
            $"FROM ZADANIA z WHERE z.USUNIETY = 0 AND z.TERMINROZPOCZECIA >= @od AND z.TERMINROZPOCZECIA < @do{Contractor("z.IDKONTRAHENTA", query)}",
            query, ct),
        MetricCatalog.CrmNewContractors => DailyAsync("c.DATAWPISU",
            "FROM KONTRAHENT c WHERE c.USUNIETY = 0 AND c.DATAWPISU >= @od AND c.DATAWPISU < @do", query, ct),
        _ => throw new ArgumentException($"Unknown CRM series '{metricKey}'.", nameof(metricKey)),
    };

    public Task<Ranking> GetRankingAsync(string metricKey, MetricQuery query, int top, CancellationToken ct = default)
    {
        var correspondence = $"{InPeriod}{Contractor("d.IDKONTRAHENTA", query)}";
        return metricKey switch
        {
            MetricCatalog.CrmByEmployee => RankingAsync("o.IMIE || ' ' || o.NAZWISKO",
                $"{Correspondence} JOIN OPERATOR o ON o.ID = d.IDOPERATORA WHERE {correspondence}", top, query, ct),
            MetricCatalog.CrmByTopic => RankingAsync("s.NAZWA",
                $"{Correspondence} JOIN SLOWNIK s ON s.ID = k.IDRODZAJAKCJI WHERE {correspondence} AND s.NAZWA > ' '", top, query, ct),
            MetricCatalog.CrmTopContractors => RankingAsync("c.SKROTNAZWY",
                $"{Correspondence} JOIN KONTRAHENT c ON c.ID = d.IDKONTRAHENTA WHERE {InPeriod}", top, query, ct),
            MetricCatalog.CrmNewByStatus => RankingAsync("s.NAZWA",
                "FROM KONTRAHENT c JOIN SLOWNIK s ON s.ID = c.STATUS WHERE c.USUNIETY = 0 AND c.DATAWPISU >= @od AND c.DATAWPISU < @do AND s.NAZWA > ' '",
                top, query, ct),
            MetricCatalog.CrmNewByType => RankingAsync("s.NAZWA",
                "FROM KONTRAHENT c JOIN SLOWNIK s ON s.ID = c.IDTYPKONTRAHENTA WHERE c.USUNIETY = 0 AND c.DATAWPISU >= @od AND c.DATAWPISU < @do AND s.NAZWA > ' '",
                top, query, ct),
            MetricCatalog.CrmOverdueTasksByEmployee => RankingAsync("o.IMIE || ' ' || o.NAZWISKO",
                $"FROM ZADANIA z JOIN OPERATOR o ON o.ID = z.IDZLECENIOBIORCY WHERE {OverdueTask}", top, query, ct),
            // Days since the last entry – a list, so no total: the rows are not parts of a whole.
            MetricCatalog.CrmNoContact => RowsAsync($"""
                SELECT FIRST {Top(top)} c.SKROTNAZWY AS NAZWA, DATEDIFF(DAY FROM MAX(d.DATAAKCJI) TO CURRENT_TIMESTAMP) AS N
                FROM KONTRAHENT c
                JOIN KORESPONDENCJADZIENNIK d ON d.IDKONTRAHENTA = c.ID
                WHERE c.USUNIETY = 0 AND COALESCE(c.ARC, 0) = 0 AND c.STATUS = @status AND d.USUNIETY = 0
                GROUP BY 1
                ORDER BY 2 DESC
                """, query, ct, ("@status", _options.DefaultContractorStatusId)),
            _ => throw new ArgumentException($"Unknown CRM ranking '{metricKey}'.", nameof(metricKey)),
        };
    }

    public async Task<decimal> GetSnapshotAsync(string metricKey, MetricQuery query, CancellationToken ct = default) => metricKey switch
    {
        MetricCatalog.CrmOverdueTasks => (await QueryAsync(
            $"SELECT COUNT(*) AS N FROM ZADANIA z WHERE {OverdueTask}{Contractor("z.IDKONTRAHENTA", query)}",
            r => (decimal)GoNetDatabase.Int(r, "N"), query, ct)).FirstOrDefault(),
        _ => throw new ArgumentException($"Unknown CRM snapshot '{metricKey}'.", nameof(metricKey)),
    };

    private int EmailType => ActionType(_options.EmailCorrespondenceActionType, nameof(GoNetOptions.EmailCorrespondenceActionType));

    private static int ActionType(int? configured, string setting) =>
        configured ?? throw new AnalyticsException($"Te dane wymagają ustawienia GoNet:{setting} w konfiguracji. Skontaktuj się z działem IT.");

    /// <summary>FIRST cannot be a parameter in Firebird 2.5 – the number is clamped and written into the query.</summary>
    private static int Top(int top) => Math.Clamp(top, 1, 100);

    /// <summary>" AND column = id" for a contractor card (the CRM id is an integer – safe to inline), empty for the whole company.</summary>
    private static string Contractor(string column, MetricQuery query) =>
        query.Contractor is { } contractor ? FormattableString.Invariant($" AND {column} = {contractor.CrmId}") : string.Empty;

    private Task<IReadOnlyList<DailyValue>> CorrespondenceAsync(int? actionType, string? direction, MetricQuery query, CancellationToken ct) =>
        DailyAsync("d.DATAAKCJI",
            $"{Correspondence} WHERE {InPeriod}{Contractor("d.IDKONTRAHENTA", query)}" +
            (actionType is null ? "" : " AND k.IDTYPAKCJI = @typ") + (direction is null ? "" : $" AND {direction}"),
            query, ct, actionType is { } type ? [("@typ", type)] : []);

    /// <summary>
    /// Rows per day. GoNet is a dialect 1 database, where DATE means a timestamp – the day is taken apart with EXTRACT.
    /// </summary>
    private async Task<IReadOnlyList<DailyValue>> DailyAsync(
        string dateColumn, string fromWhere, MetricQuery query, CancellationToken ct, params (string, object?)[] extra) =>
        await QueryAsync($"""
            SELECT EXTRACT(YEAR FROM {dateColumn}) AS Y, EXTRACT(MONTH FROM {dateColumn}) AS M, EXTRACT(DAY FROM {dateColumn}) AS D, COUNT(*) AS N
            {fromWhere}
            GROUP BY 1, 2, 3
            """,
            r => new DailyValue(new DateOnly(GoNetDatabase.Int(r, "Y"), GoNetDatabase.Int(r, "M"), GoNetDatabase.Int(r, "D")), GoNetDatabase.Int(r, "N")),
            query, ct, extra);

    /// <summary>Counts rows by a name – the top names and, with a second query, the total over all names.</summary>
    private async Task<Ranking> RankingAsync(string name, string fromWhere, int top, MetricQuery query, CancellationToken ct)
    {
        var rows = await RowsAsync($"""
            SELECT FIRST {Top(top)} {name} AS NAZWA, COUNT(*) AS N
            {fromWhere}
            GROUP BY 1
            ORDER BY 2 DESC
            """, query, ct);

        var total = await QueryAsync($"SELECT COUNT(*) AS N {fromWhere}", r => (decimal)GoNetDatabase.Int(r, "N"), query, ct);
        return rows with { Total = total.FirstOrDefault() };
    }

    private async Task<Ranking> RowsAsync(string sql, MetricQuery query, CancellationToken ct, params (string, object?)[] extra) =>
        new(await QueryAsync(sql, r => new RankedValue(GoNetDatabase.Text(r, "NAZWA") ?? "(brak)", GoNetDatabase.Int(r, "N")), query, ct, extra));

    private async Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql, Func<FbDataReader, T> map, MetricQuery query, CancellationToken ct, params (string, object?)[] extra)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct);

            // Only the parameters the query uses – Firebird rejects unknown ones.
            (string, object?)[] parameters =
            [
                ("@od", query.Range.From.ToDateTime(TimeOnly.MinValue)),
                ("@do", query.Range.To.AddDays(1).ToDateTime(TimeOnly.MinValue)),
                .. extra,
            ];
            await using var command = GoNetDatabase.Command(connection, null, sql, [.. parameters.Where(p => sql.Contains(p.Item1, StringComparison.Ordinal))]);
            command.CommandTimeout = 60;

            var rows = new List<T>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(map(reader));
            }

            return rows;
        }
        catch (CrmException ex)
        {
            throw new AnalyticsException(ex.Message, inner: ex);
        }
        catch (FbException ex)
        {
            logger.LogError(ex, "Analytics query failed (GoNet)");
            throw new AnalyticsException("Nie udało się pobrać danych z GoNet. Spróbuj za chwilę.", inner: ex);
        }
    }
}

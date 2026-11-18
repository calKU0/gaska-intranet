using System.Globalization;
using Gaska.Intranet.Core.Analytics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Gaska.Intranet.Infrastructure.Analytics;

/// <summary>
/// Read-only access to a SQL Server database of another system (ERP, B2B) through one of the named connection strings –
/// the first one that is configured is used.
/// </summary>
internal sealed class SqlSource
{
    private readonly string? _connectionString;
    private readonly string _displayName;
    private readonly ILogger _logger;

    /// <param name="connectionStringNames">Names under <c>ConnectionStrings</c>, most specific first.</param>
    public SqlSource(IConfiguration configuration, string displayName, ILogger logger, params string[] connectionStringNames)
    {
        _displayName = displayName;
        _logger = logger;

        var configured = connectionStringNames
            .Select(configuration.GetConnectionString)
            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
        _connectionString = configured is null
            ? null
            : new SqlConnectionStringBuilder(configured) { ApplicationIntent = ApplicationIntent.ReadOnly }.ConnectionString;
    }

    public bool IsConfigured => _connectionString is not null;

    /// <summary>Runs a query and maps every row; database errors become a message safe to show to the user.</summary>
    public async Task<List<T>> QueryAsync<T>(
        string sql, Func<SqlDataReader, T> map, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        if (!IsConfigured)
        {
            throw new AnalyticsException($"Źródło danych „{_displayName}” nie jest skonfigurowane. Skontaktuj się z działem IT.");
        }

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 60;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            var rows = new List<T>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(map(reader));
            }

            return rows;
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "Analytics query failed ({Source})", _displayName);
            throw new AnalyticsException($"Nie udało się pobrać danych ({_displayName}). Spróbuj za chwilę.", inner: ex);
        }
    }

    /// <summary>
    /// Runs a ranking: <paramref name="grouped"/> returns one row per name as <c>(label, detail, v)</c>; the top rows by
    /// value come back together with the total over all names.
    /// </summary>
    public async Task<Ranking> RankingAsync(string grouped, int top, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        var rows = await QueryAsync($"""
            SELECT TOP (@top) x.label, x.detail, x.v, SUM(x.v) OVER ()
            FROM ({grouped}) x
            ORDER BY x.v DESC, x.label
            """,
            r => (Row: new RankedValue(Text(r, 0), Number(r, 2), NullIfEmpty(Text(r, 1))), Total: Number(r, 3)),
            ct, [.. parameters, ("@top", top)]);

        return rows.Count == 0 ? Ranking.Empty : new Ranking(rows.Select(r => r.Row).ToList(), rows[0].Total);
    }

    public static decimal Number(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0m : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    public static string Text(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? string.Empty : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)!.Trim();

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;

    /// <summary>"1, 2, 3" for an IN list – integers only, so it is safe to put into the query text.</summary>
    public static string Ids(IEnumerable<int> ids) => string.Join(", ", ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));

    /// <summary>"@p0, @p1" with its parameters – for IN lists of texts from configuration, never concatenated into SQL.</summary>
    public static (string List, (string Name, object Value)[] Parameters) TextList(string prefix, IReadOnlyList<string> values)
    {
        var parameters = values.Select((v, i) => ($"@{prefix}{i}", (object)v)).ToArray();
        return (string.Join(", ", parameters.Select(p => p.Item1)), parameters);
    }
}

/// <summary>Comarch ERP XL stores dates as the number of days since 1800-12-28 (Clarion dates).</summary>
internal static class ClarionDate
{
    private static readonly int Epoch = new DateOnly(1800, 12, 28).DayNumber;

    public static int From(DateOnly day) => day.DayNumber - Epoch;

    public static DateOnly ToDate(int clarion) => DateOnly.FromDayNumber(Epoch + clarion);
}

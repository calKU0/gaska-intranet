using System.Data;
using System.Globalization;
using Gaska.Intranet.Core.Rcp;
using Gaska.Intranet.Core.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Infrastructure.Rcp;

public sealed class RcpOptions
{
    public const string SectionName = "Rcp";

    /// <summary>SQL Server linked server pointing to the Punktualnik PostgreSQL database.</summary>
    public string LinkedServer { get; set; } = "PUNKTUALNIK";

    /// <summary>
    /// Manual links "domain account → idx_osoby" for people whose name differs between AD and Punktualnik
    /// (or two people with the same name).
    /// </summary>
    public Dictionary<string, int> Employees { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Punktualnik time clock accessed through the XL SQL Server (linked server, same procedures as the Hydra RCP add-on):
/// reads <c>public.att_log</c> via OPENQUERY and punches with <c>dbo.Gaska*Punktualnik</c> procedures.
/// </summary>
internal sealed class PunktualnikRcpService(
    IConfiguration configuration,
    IOptions<RcpOptions> options,
    IMemoryCache cache,
    TimeProvider clock,
    ILogger<PunktualnikRcpService> logger) : IRcpService
{
    private const string EmployeesCacheKey = "rcp:employees";

    private static readonly IReadOnlyDictionary<RcpEventType, string> PunchProcedures = new Dictionary<RcpEventType, string>
    {
        [RcpEventType.WorkStart] = "dbo.GaskaRozpocznijPracePunktualnik",
        [RcpEventType.WorkEnd] = "dbo.GaskaZakonczPracePunktualnik",
        [RcpEventType.BreakStart] = "dbo.GaskaRozpocznijPrzerwePunktualnik",
        [RcpEventType.BreakEnd] = "dbo.GaskaZakonczPrzerwePunktualnik",
    };

    // XL SQL Server that has the Punktualnik linked server – by default the application database.
    private readonly string _connectionString = configuration.GetConnectionString("Rcp")
        ?? configuration.GetConnectionString("Intranet")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:Intranet.");

    public async Task<RcpEmployee?> FindEmployeeAsync(string accountName, string displayName, CancellationToken ct = default)
    {
        var employees = await GetEmployeesAsync(ct);

        if (options.Value.Employees.TryGetValue(accountName, out var configuredId))
        {
            return employees.FirstOrDefault(e => e.Id == configuredId) is { } configured
                ? new RcpEmployee(configured.Id, configured.FullName)
                : null;
        }

        // AD display name "Jan Kowalski" (sometimes "Kowalski Jan") ↔ Punktualnik first + last name.
        var name = Key(displayName);
        var matches = employees.Where(e => e.Keys.Contains(name)).ToList();
        if (matches.Count > 1)
        {
            logger.LogWarning("Several Punktualnik people match {Account} ({Name}) – configure Rcp:Employees.", accountName, displayName);
            return null;
        }

        return matches.Count == 1 ? new RcpEmployee(matches[0].Id, matches[0].FullName) : null;
    }

    public async Task<IReadOnlyList<RcpDay>> GetDaysAsync(int employeeId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        // OPENQUERY accepts only a literal, so the query is composed – exclusively from an integer and two dates.
        var remote = string.Create(CultureInfo.InvariantCulture, $"""
            SELECT a.data_czas, d.idx_wartosci
            FROM public.att_log a
            JOIN dev.klawisze_funkc d ON a.idx_device = d.idx_device AND a.in_out = d.wartosc
            WHERE a.idx_osoby = {employeeId}
              AND a.aktywny = true
              AND d.idx_wartosci IN (0, 1, 2, 3)
              AND a.data_czas >= '{from:yyyy-MM-dd}' AND a.data_czas < '{to.AddDays(1):yyyy-MM-dd}'
            """);

        var events = new List<RcpEvent>();
        await ExecuteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT data_czas, idx_wartosci FROM OPENQUERY({LinkedServer}, '{remote.Replace("'", "''")}')";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                events.Add(new RcpEvent(reader.GetDateTime(0), (RcpEventType)Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture)));
            }
        }, "Nie udało się pobrać odbić z Punktualnika.");

        return events
            .GroupBy(e => DateOnly.FromDateTime(e.Time))
            .OrderByDescending(g => g.Key)
            .Select(g => new RcpDay(g.Key, g))
            .ToList();
    }

    public async Task<RcpDay> PunchAsync(int employeeId, RcpEventType type, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var day = await GetTodayAsync(employeeId, today, ct);

        // Re-checked on fresh data: a double click or a second open tab must not add a second entry.
        if (!day.CanPunch(type))
        {
            throw new RcpException(day.State switch
            {
                RcpState.Working => "Praca jest już rozpoczęta.",
                RcpState.OnBreak => "Trwa przerwa – najpierw ją zakończ.",
                RcpState.Finished => "Praca na dziś jest już zakończona.",
                _ => "Najpierw rozpocznij pracę.",
            });
        }

        await ExecuteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = PunchProcedures[type];
            command.CommandType = CommandType.StoredProcedure;
            command.Parameters.Add(new SqlParameter("@userId", SqlDbType.NVarChar, 10) { Value = employeeId.ToString(CultureInfo.InvariantCulture) });
            await command.ExecuteNonQueryAsync(ct);
        }, "Nie udało się zapisać odbicia w Punktualniku.");

        logger.LogInformation("RCP punch {Type} for Punktualnik person {EmployeeId}.", type, employeeId);
        return await GetTodayAsync(employeeId, today, ct);
    }

    private async Task<RcpDay> GetTodayAsync(int employeeId, DateOnly today, CancellationToken ct) =>
        (await GetDaysAsync(employeeId, today, today, ct)).FirstOrDefault() ?? new RcpDay(today, []);

    /// <summary>Active Punktualnik people – a small list, cached for a while.</summary>
    private async Task<IReadOnlyList<PunktualnikPerson>> GetEmployeesAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(EmployeesCacheKey, out IReadOnlyList<PunktualnikPerson>? cached) && cached is not null)
        {
            return cached;
        }

        var people = new List<PunktualnikPerson>();
        await ExecuteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT idx_osoby, imie, nazwisko
                FROM OPENQUERY({LinkedServer}, 'SELECT idx_osoby, imie, nazwisko FROM public.users WHERE aktywny = true')
                """;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var first = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
                var last = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim();
                people.Add(new PunktualnikPerson(reader.GetInt32(0), $"{first} {last}".Trim(),
                    [Key($"{first} {last}"), Key($"{last} {first}")]));
            }
        }, "Nie udało się pobrać listy pracowników z Punktualnika.");

        cache.Set(EmployeesCacheKey, (IReadOnlyList<PunktualnikPerson>)people, TimeSpan.FromMinutes(15));
        return people;
    }

    private async Task ExecuteAsync(Func<SqlConnection, Task> action, string userMessage)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            await action(connection);
        }
        catch (SqlException ex)
        {
            logger.LogError(ex, "Punktualnik (linked server {LinkedServer}) failed.", LinkedServer);
            throw new RcpException(userMessage + " Spróbuj ponownie lub zgłoś to do działu IT.", ex);
        }
    }

    /// <summary>Linked server name is configuration, but it still ends up in SQL – allow only a plain identifier.</summary>
    private string LinkedServer =>
        options.Value.LinkedServer.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            ? $"[{options.Value.LinkedServer}]"
            : throw new InvalidOperationException("Rcp:LinkedServer must be a plain identifier.");

    private static string Key(string name) =>
        string.Join(' ', TextSearch.Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private sealed record PunktualnikPerson(int Id, string FullName, IReadOnlyList<string> Keys);
}

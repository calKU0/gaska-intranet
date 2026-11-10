using FirebirdSql.Data.FirebirdClient;
using Gaska.Intranet.Core.Crm;
using Gaska.Intranet.Core.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Infrastructure.GoNet;

/// <summary>GoNet dictionaries (SLOWNIK), table numbers and operators.</summary>
internal sealed class GoNetDictionary(GoNetDatabase db, IMemoryCache cache, IOptions<GoNetOptions> options) : ICrmDirectory
{
    private readonly GoNetOptions _options = options.Value;

    public Task<IReadOnlyList<GoNetOperator>> GetOperatorsAsync(CancellationToken ct = default) =>
        cache.GetOrCreateAsync("gonet:operators", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(_options.OperatorCacheMinutes);
            await using var connection = await db.OpenAsync(ct);
            await using var command = GoNetDatabase.Command(connection, null,
                "SELECT ID, IMIE, NAZWISKO, LOGIN FROM OPERATOR WHERE USUNIETY = 0 ORDER BY NAZWISKO, IMIE");

            var list = new List<GoNetOperator>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new GoNetOperator(
                    GoNetDatabase.Int(reader, "ID"),
                    GoNetDatabase.Text(reader, "IMIE") ?? string.Empty,
                    GoNetDatabase.Text(reader, "NAZWISKO") ?? string.Empty,
                    GoNetDatabase.Text(reader, "LOGIN")));
            }

            return (IReadOnlyList<GoNetOperator>)list;
        })!;

    private static readonly string[] PolishVoivodeships =
    [
        "DOLNOŚLĄSKIE", "KUJAWSKO-POMORSKIE", "LUBELSKIE", "LUBUSKIE", "ŁÓDZKIE", "MAŁOPOLSKIE", "MAZOWIECKIE", "OPOLSKIE",
        "PODKARPACKIE", "PODLASKIE", "POMORSKIE", "ŚLĄSKIE", "ŚWIĘTOKRZYSKIE", "WARMIŃSKO-MAZURSKIE", "WIELKOPOLSKIE",
        "ZACHODNIOPOMORSKIE",
    ];

    /// <summary>
    /// GoNet's voivodeship dictionary also holds hundreds of foreign regions (and duplicates) – only the 16 Polish
    /// voivodeships are offered, each with the id most used by contractors.
    /// </summary>
    public Task<IReadOnlyList<DictionaryItem>> GetVoivodeshipsAsync(CancellationToken ct = default) =>
        CachedAsync("gonet:voivodeships", TimeSpan.FromHours(12), async () =>
        {
            var all = await QueryDictionaryAsync(_options.VoivodeshipDictionaryType,
                "SELECT IDWOJEWODZTWO AS ID, COUNT(*) AS C FROM KONTRAHENT GROUP BY IDWOJEWODZTWO", ct);

            return (IReadOnlyList<DictionaryItem>)PolishVoivodeships
                .Select(name => all
                    .Where(i => TextSearch.Normalize(i.Item.Name) == TextSearch.Normalize(name))
                    .OrderByDescending(i => i.Usage).ThenBy(i => i.Item.Id)
                    .Select(i => i.Item)
                    .FirstOrDefault())
                .Where(i => i is not null)
                .Select(i => i!)
                .ToList();
        }, ct);

    /// <summary>
    /// The country dictionary has duplicates (e.g. "PL" next to "POLSKA") – each name once, with the id most used by
    /// contractors; two-letter codes are skipped. Polska first, the rest alphabetically.
    /// </summary>
    public Task<IReadOnlyList<DictionaryItem>> GetCountriesAsync(CancellationToken ct = default) =>
        CachedAsync("gonet:countries", TimeSpan.FromHours(12), async () =>
        {
            var all = await QueryDictionaryAsync(_options.CountryDictionaryType,
                "SELECT IDKRAJ AS ID, COUNT(*) AS C FROM KONTRAHENT GROUP BY IDKRAJ", ct);

            return (IReadOnlyList<DictionaryItem>)all
                .Where(i => i.Item.Name.Trim().Length > 2)
                .GroupBy(i => TextSearch.Normalize(i.Item.Name))
                .Select(g => g.OrderByDescending(i => i.Item.Id == _options.DefaultCountryId).ThenByDescending(i => i.Usage).ThenBy(i => i.Item.Id).First().Item)
                .OrderByDescending(i => i.Id == _options.DefaultCountryId)
                .ThenBy(i => i.Name, StringComparer.Create(new System.Globalization.CultureInfo("pl-PL"), true))
                .ToList();
        }, ct);

    public Task<IReadOnlyList<DictionaryItem>> GetContractorTypesAsync(CancellationToken ct = default) =>
        SimpleDictionaryAsync("gonet:contractor-types", _options.ContractorTypeDictionaryType, ct);

    public Task<IReadOnlyList<DictionaryItem>> GetContractorStatusesAsync(CancellationToken ct = default) =>
        SimpleDictionaryAsync("gonet:contractor-statuses", _options.ContractorStatusDictionaryType, ct);

    /// <summary>Task kinds used in the last two years, most used first (the dictionary holds many old campaigns).</summary>
    public Task<IReadOnlyList<DictionaryItem>> GetTaskTopicsAsync(CancellationToken ct = default) =>
        CachedAsync("gonet:task-topics", TimeSpan.FromHours(6), async () =>
        {
            var since = DateTime.Today.AddYears(-2);
            var items = await QueryDictionaryAsync(_options.TaskKindDictionaryType,
                $"SELECT IDRODZAJZADANIA AS ID, COUNT(*) AS C FROM ZADANIA WHERE DATAWPISU >= '{since:dd.MM.yyyy}' GROUP BY IDRODZAJZADANIA", ct);

            return (IReadOnlyList<DictionaryItem>)items
                .Where(i => !string.IsNullOrWhiteSpace(i.Item.Name) && (i.Usage > 0 || i.Item.Id == _options.StartVisitKindId))
                .OrderByDescending(i => i.Item.Id == _options.StartVisitKindId)
                .ThenByDescending(i => i.Usage)
                .Select(i => i.Item)
                .ToList();
        }, ct);

    private Task<IReadOnlyList<DictionaryItem>> SimpleDictionaryAsync(string key, int type, CancellationToken ct) =>
        CachedAsync(key, TimeSpan.FromHours(12), async () =>
            (IReadOnlyList<DictionaryItem>)(await QueryDictionaryAsync(type, usageSql: null, ct))
            .Select(i => i.Item)
            .Where(i => !string.IsNullOrWhiteSpace(i.Name))
            .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList(), ct);

    /// <summary>Dictionary entries of a type with optional usage counts ("SELECT ID, C … GROUP BY" query).</summary>
    private async Task<List<(DictionaryItem Item, int Usage)>> QueryDictionaryAsync(int type, string? usageSql, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);

        var usage = new Dictionary<int, int>();
        if (usageSql is not null)
        {
            await using var usageCommand = GoNetDatabase.Command(connection, null, usageSql);
            await using var usageReader = await usageCommand.ExecuteReaderAsync(ct);
            while (await usageReader.ReadAsync(ct))
            {
                usage[GoNetDatabase.Int(usageReader, "ID")] = GoNetDatabase.Int(usageReader, "C");
            }
        }

        await using var command = GoNetDatabase.Command(connection, null,
            "SELECT ID, NAZWA FROM SLOWNIK WHERE IDTYPU = @typ AND USUNIETY = 0", ("@typ", type));
        var list = new List<(DictionaryItem, int)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = GoNetDatabase.Int(reader, "ID");
            list.Add((new DictionaryItem(id, GoNetDatabase.Text(reader, "NAZWA") ?? string.Empty), usage.GetValueOrDefault(id)));
        }

        return list;
    }

    private async Task<T> CachedAsync<T>(string key, TimeSpan lifetime, Func<Task<T>> factory, CancellationToken ct)
    {
        if (cache.TryGetValue(key, out T? value) && value is not null)
        {
            return value;
        }

        value = await factory();
        cache.Set(key, value, lifetime);
        return value;
    }

    public async Task<GoNetOperator?> FindOperatorAsync(string displayName, CancellationToken ct = default)
    {
        var name = TextSearch.Normalize(displayName);
        if (name.Length == 0)
        {
            return null;
        }

        // "Jan Kowalski" or "Kowalski Jan" – only a unique match counts.
        var matches = (await GetOperatorsAsync(ct))
            .Where(o => TextSearch.Normalize($"{o.FirstName} {o.LastName}") == name ||
                        TextSearch.Normalize($"{o.LastName} {o.FirstName}") == name)
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>Dictionary entry id for a name (e.g. a city); adds the entry when missing. Empty name → -1.</summary>
    public async Task<int> GetOrAddAsync(FbConnection connection, FbTransaction tx, int dictionaryType, string? name, int operatorId, CancellationToken ct)
    {
        var clean = GoNetDatabase.Clean(name, 80).ToUpperInvariant();
        if (clean.Length == 0)
        {
            return -1;
        }

        await using var command = GoNetDatabase.Command(connection, tx,
            "EXECUTE PROCEDURE SP_WYDAJDODAJPOZYCJESLOWNIKA(@typ, @nazwa, @oddzial, @operator)",
            ("@typ", dictionaryType), ("@nazwa", clean), ("@oddzial", _options.BranchId), ("@operator", operatorId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    /// <summary>GoNet table number (used by the change log).</summary>
    public Task<int> TableIdAsync(FbConnection connection, FbTransaction tx, string table, CancellationToken ct) =>
        cache.GetOrCreateAsync($"gonet:table:{table}", async _ =>
        {
            await using var command = GoNetDatabase.Command(connection, tx, "EXECUTE PROCEDURE SP_IDTABELI(@t)", ("@t", table));
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        })!;
}

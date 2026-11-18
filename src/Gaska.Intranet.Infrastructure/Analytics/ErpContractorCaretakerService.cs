using System.Globalization;
using Gaska.Intranet.Core.Analytics;
using Gaska.Intranet.Core.Crm;
using Gaska.Intranet.Infrastructure.GoNet;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Gaska.Intranet.Infrastructure.Analytics;

/// <summary>
/// Caretakers of contractors from Comarch ERP XL. The contractor's card is matched like the turnover's (acronym, then tax
/// number); its caretakers are those of its sales region (CDN.KntRejony → CDN.KntOpiekun with KtO_KntTyp 948) and those
/// set on the card itself (KtO_KntTyp 32). Only assignments valid today count. One query per page of the list; filtering
/// by caretakers uses an index of all contractors (GoNet) with their caretakers (ERP), rebuilt every 10 minutes.
/// </summary>
internal sealed class ErpContractorCaretakerService(
    IConfiguration configuration, GoNetContractorService goNet, IMemoryCache cache, TimeProvider clock,
    ILogger<ErpContractorCaretakerService> logger) : IContractorCaretakerService
{
    private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(10);

    /// <summary>CDN.KntKarty.Knt_Typ of a supplier (16 = customer, 24 = customer and supplier).</summary>
    private const int SupplierCardType = 8;
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private volatile Index? _index;
    private DateTimeOffset _indexValidUntil;

    /// <summary>The caretakers shown for every active GoNet contractor, and everyone named there.</summary>
    private sealed record Index(IReadOnlyDictionary<int, ContractorCaretakers> Contractors, IReadOnlyList<string> Names);
    private readonly SqlSource _erp = new(configuration, "ERP XL", logger, ErpOptions.ConnectionStrings);

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<CaretakerAssignment>>> GetAsync(
        IReadOnlyList<ContractorKey> contractors, CancellationToken ct = default)
    {
        var result = new Dictionary<int, IReadOnlyList<CaretakerAssignment>>();
        var missing = new List<ContractorKey>();
        foreach (var contractor in contractors.DistinctBy(c => c.CrmId))
        {
            if (cache.TryGetValue(CacheKey(contractor), out IReadOnlyList<CaretakerAssignment>? cached) && cached is not null)
            {
                if (cached.Count > 0)
                {
                    result[contractor.CrmId] = cached;
                }
            }
            else
            {
                missing.Add(contractor);
            }
        }

        if (missing.Count == 0 || !_erp.IsConfigured)
        {
            return result;
        }

        // Rows of the input table: (CRM id, acronym, tax number of digits and letters only). varchar, as the ERP's
        // columns – an nvarchar parameter would make SQL Server convert the column and give up its index.
        var parameters = new List<(string, object)> { ("@today", ClarionDate.From(DateOnly.FromDateTime(clock.GetLocalNow().DateTime))) };
        var rows = new List<string>();
        for (var i = 0; i < missing.Count; i++)
        {
            var nip = TaxNumber(missing[i].Nip);
            parameters.Add(($"@c{i}", missing[i].CrmId));
            parameters.Add(($"@a{i}", missing[i].ShortName.Trim()));
            parameters.Add(($"@n{i}", nip.Count(char.IsDigit) >= 8 ? nip : string.Empty));
            rows.Add(string.Create(CultureInfo.InvariantCulture, $"(@c{i}, CAST(@a{i} AS varchar(100)), CAST(@n{i} AS varchar(40)))"));
        }

        List<(int CrmId, CaretakerAssignment Assignment)> found;
        try
        {
            found = await _erp.QueryAsync($"""
                SET NOCOUNT ON;
                DECLARE @input TABLE (CrmId int, Acronym varchar(100), Nip varchar(40));
                INSERT INTO @input VALUES {string.Join(", ", rows)};

                WITH byAcronym AS (
                    SELECT i.CrmId, k.Knt_GIDNumer AS Card, k.Knt_Typ AS Typ FROM @input i JOIN CDN.KntKarty k ON k.Knt_Akronim = i.Acronym
                ),
                cards AS (
                    SELECT CrmId, Card, Typ FROM byAcronym
                    UNION
                    SELECT i.CrmId, k.Knt_GIDNumer, k.Knt_Typ
                    FROM @input i
                    JOIN CDN.KntKarty k ON REPLACE(REPLACE(k.Knt_Nip, '-', ''), ' ', '') = i.Nip
                    WHERE i.Nip <> '' AND NOT EXISTS (SELECT 1 FROM byAcronym b WHERE b.CrmId = i.CrmId)
                ),
                active AS (
                    SELECT o.KtO_KntTyp, o.KtO_KntNumer, o.KtO_PrcNumer, o.KtO_Glowny
                    FROM CDN.KntOpiekun o
                    WHERE (o.KtO_DataOd = 0 OR o.KtO_DataOd <= @today) AND (o.KtO_DataDo = 0 OR o.KtO_DataDo >= @today)
                )
                SELECT c.CrmId, 0, o.KtO_Glowny, p.Prc_Imie1, p.Prc_Nazwisko, c.Typ
                FROM cards c
                JOIN CDN.KntRejony r ON r.KnR_KntTyp = 32 AND r.KnR_KntNumer = c.Card
                JOIN active o ON o.KtO_KntTyp = 948 AND o.KtO_KntNumer = r.KnR_Rejon
                JOIN CDN.PrcKarty p ON p.Prc_GIDNumer = o.KtO_PrcNumer
                UNION ALL
                SELECT c.CrmId, 1, o.KtO_Glowny, p.Prc_Imie1, p.Prc_Nazwisko, c.Typ
                FROM cards c
                JOIN active o ON o.KtO_KntTyp = 32 AND o.KtO_KntNumer = c.Card
                JOIN CDN.PrcKarty p ON p.Prc_GIDNumer = o.KtO_PrcNumer
                """,
                r => (r.GetInt32(0), new CaretakerAssignment(
                    $"{SqlSource.Text(r, 3)} {SqlSource.Text(r, 4)}".Trim(),
                    SqlSource.Number(r, 2) == 1,
                    SqlSource.Number(r, 1) == 0 ? CaretakerSource.Region : CaretakerSource.Contractor,
                    SqlSource.Number(r, 5) == SupplierCardType)),
                ct, [.. parameters]);
        }
        catch (AnalyticsException ex)
        {
            throw new CrmException("Nie udało się pobrać opiekunów kontrahentów z ERP XL.", ex);
        }

        var byContractor = found.ToLookup(f => f.CrmId, f => f.Assignment);
        foreach (var contractor in missing)
        {
            IReadOnlyList<CaretakerAssignment> assignments = byContractor[contractor.CrmId].ToList();
            cache.Set(CacheKey(contractor), assignments, CacheTime);
            if (assignments.Count > 0)
            {
                result[contractor.CrmId] = assignments;
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<string>> GetNamesAsync(CancellationToken ct = default) => (await GetIndexAsync(ct)).Names;

    public async Task<IReadOnlySet<int>> FindContractorsAsync(IReadOnlyCollection<string> names, CancellationToken ct = default)
    {
        var wanted = names.Select(PersonNames.Normalize).Where(n => n.Length > 0).ToHashSet();
        if (wanted.Count == 0)
        {
            return new HashSet<int>();
        }

        var index = await GetIndexAsync(ct);
        return index.Contractors
            .Where(c => c.Value.AccountManagers.Concat(c.Value.SalesRepresentatives).Any(n => wanted.Contains(PersonNames.Normalize(n))))
            .Select(c => c.Key)
            .ToHashSet();
    }

    /// <summary>
    /// The index; reading all 40 000 contractors from GoNet takes several seconds, so after the first build an outdated
    /// index is still answered with while a fresh one is built in the background.
    /// </summary>
    private async Task<Index> GetIndexAsync(CancellationToken ct)
    {
        var current = _index;
        if (current is not null)
        {
            if (clock.GetUtcNow() >= _indexValidUntil && _indexLock.Wait(0))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await RebuildAsync(CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Refreshing the caretaker index failed – the previous one stays");
                    }
                    finally
                    {
                        _indexLock.Release();
                    }
                }, CancellationToken.None);
            }

            return current;
        }

        await _indexLock.WaitAsync(ct);
        try
        {
            return _index ?? await RebuildAsync(ct);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private async Task<Index> RebuildAsync(CancellationToken ct)
    {
        var (index, complete) = await BuildIndexAsync(ct);
        _index = index;

        // Without the ERP only GoNet's managers are known - try again soon instead of in 10 minutes.
        _indexValidUntil = clock.GetUtcNow() + (complete ? CacheTime : TimeSpan.FromMinutes(1));
        return index;
    }

    private async Task<(Index Index, bool Complete)> BuildIndexAsync(CancellationToken ct)
    {
        var contractors = await goNet.ListAllAsync(ct);

        var byAcronym = new Dictionary<string, List<CaretakerAssignment>>(StringComparer.OrdinalIgnoreCase);
        var byNip = new Dictionary<string, List<CaretakerAssignment>>(StringComparer.OrdinalIgnoreCase);
        var complete = true;
        if (_erp.IsConfigured)
        {
            try
            {
                var rows = await _erp.QueryAsync("""
                    WITH active AS (
                        SELECT o.KtO_KntTyp, o.KtO_KntNumer, o.KtO_PrcNumer, o.KtO_Glowny
                        FROM CDN.KntOpiekun o
                        WHERE (o.KtO_DataOd = 0 OR o.KtO_DataOd <= @today) AND (o.KtO_DataDo = 0 OR o.KtO_DataDo >= @today)
                    )
                    SELECT k.Knt_Akronim, k.Knt_Nip, 0, o.KtO_Glowny, p.Prc_Imie1, p.Prc_Nazwisko, k.Knt_Typ
                    FROM CDN.KntKarty k
                    JOIN CDN.KntRejony r ON r.KnR_KntTyp = 32 AND r.KnR_KntNumer = k.Knt_GIDNumer
                    JOIN active o ON o.KtO_KntTyp = 948 AND o.KtO_KntNumer = r.KnR_Rejon
                    JOIN CDN.PrcKarty p ON p.Prc_GIDNumer = o.KtO_PrcNumer
                    UNION ALL
                    SELECT k.Knt_Akronim, k.Knt_Nip, 1, o.KtO_Glowny, p.Prc_Imie1, p.Prc_Nazwisko, k.Knt_Typ
                    FROM CDN.KntKarty k
                    JOIN active o ON o.KtO_KntTyp = 32 AND o.KtO_KntNumer = k.Knt_GIDNumer
                    JOIN CDN.PrcKarty p ON p.Prc_GIDNumer = o.KtO_PrcNumer
                    """,
                    r => (Acronym: SqlSource.Text(r, 0), Nip: TaxNumber(SqlSource.Text(r, 1)), Assignment: new CaretakerAssignment(
                        $"{SqlSource.Text(r, 4)} {SqlSource.Text(r, 5)}".Trim(),
                        SqlSource.Number(r, 3) == 1,
                        SqlSource.Number(r, 2) == 0 ? CaretakerSource.Region : CaretakerSource.Contractor,
                        SqlSource.Number(r, 6) == SupplierCardType)),
                    ct, ("@today", ClarionDate.From(DateOnly.FromDateTime(clock.GetLocalNow().DateTime))));

                foreach (var row in rows)
                {
                    Add(byAcronym, row.Acronym, row.Assignment);
                    if (row.Nip.Count(char.IsDigit) >= 8)
                    {
                        Add(byNip, row.Nip, row.Assignment);
                    }
                }
            }
            catch (AnalyticsException ex)
            {
                logger.LogWarning(ex, "Caretaker index built without the ERP");
                complete = false;
            }
        }

        // The same matching as for one contractor: its acronym, or - when no card has it - its tax number.
        var result = new Dictionary<int, ContractorCaretakers>(contractors.Count);
        foreach (var c in contractors)
        {
            var nip = TaxNumber(c.Nip);
            var assignments = byAcronym.GetValueOrDefault(c.ShortName.Trim())
                ?? (nip.Count(char.IsDigit) >= 8 ? byNip.GetValueOrDefault(nip) : null)
                ?? [];
            result[c.Id] = ContractorCaretakers.From(assignments, c.Manager);
        }

        var names = result.Values
            .SelectMany(c => c.AccountManagers.Concat(c.SalesRepresentatives))
            .GroupBy(PersonNames.Normalize)
            .Select(g => g.First())
            .OrderBy(n => n, StringComparer.Create(CultureInfo.GetCultureInfo("pl-PL"), ignoreCase: true))
            .ToList();

        return (new Index(result, names), complete);
    }

    private static void Add(Dictionary<string, List<CaretakerAssignment>> map, string key, CaretakerAssignment assignment)
    {
        if (key.Length == 0)
        {
            return;
        }

        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = [];
        }

        list.Add(assignment);
    }

    private static string TaxNumber(string? nip) => new((nip ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());

    private static string CacheKey(ContractorKey contractor) => $"erp:caretakers:{contractor.CrmId}:{contractor.ShortName}|{contractor.Nip}";
}

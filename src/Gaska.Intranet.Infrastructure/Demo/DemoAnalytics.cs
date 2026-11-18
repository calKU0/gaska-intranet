using System.Security.Claims;
using Gaska.Intranet.Core.Access;
using Gaska.Intranet.Core.Analytics;

namespace Gaska.Intranet.Infrastructure.Demo;

/// <summary>
/// Demo chart data (Development + Demo:Enabled) instead of the ERP, GoNet and B2B: made-up but stable numbers with a
/// yearly rhythm, a slow growth and quiet weekends – for the user manual's screenshots and UI work without the company
/// systems. The same day always gives the same value, so screenshots can be repeated.
/// </summary>
internal sealed class DemoAnalytics(IModuleAccessService access) : IMetricSource, IContractorTurnoverService, IContractorLinks
{
    private static readonly string[] Customers =
        ["AGROMAX", "ROLTECH", "FARM-PARTS", "MECHANIKA KRAK", "GOSP. ROLNE KOWALCZYK", "AGRO-SERWIS", "TRAKTOR-CZĘŚCI", "ZIELONE POLE", "KOMBAJN-EXPERT", "ROLMASZ", "POLNA 12", "AGRI-PARTNER"];

    private static readonly (string Code, string Name)[] Products =
    [
        ("643656.01", "Przekładnia napędu kosy"), ("736872.12", "Nóż sieczkarni"), ("656860.05", "Pompa hydrauliczna"), ("644905.01", "Koło pasowe"),
        ("6205-2RS", "Łożysko kulkowe 6205"), ("SPB-2500", "Pas klinowy"), ("HF-35153", "Filtr oleju"), ("H385202051020", "Termostat"),
        ("809319.3", "Palec podbieracza"), ("GR.G1F/HD", "Smarownica ręczna"), ("10B-1-5M", "Łańcuch rolkowy"), ("WPT-610", "Wał przegubowy"),
    ];

    private static readonly string[] Employees = ["Adam Administrator", "Jan Kowalski", "Anna Nowak", "Piotr Wiśniewski", "Ewa Lewandowska", "Marek Zając"];
    private static readonly string[] Topics = ["OFERTA", "ZAPYTANIE", "WIZYTA STARTOWA", "REKLAMACJE", "B2B", "POZYSKANIE KLIENTA", "SPRAWY FIRMOWE"];
    private static readonly string[] Countries = ["PL", "DE", "RO", "LT", "CZ", "UA", "IT", "HU"];
    private static readonly string[] Searches = ["611203", "PAS KLINOWY", "736872", "ŁOŻYSKO 6205", "NÓŻ", "694573", "FILTR OLEJU", "TERMOSTAT", "PALEC", "644905", "ŁAŃCUCH", "POMPA"];
    private static readonly string[] Reasons = ["NIE DZIAŁA", "CIEKNIE", "USZKODZENIE W TRANSPORCIE", "SŁABA JAKOŚĆ", "PĘKNIĘCIE PODCZAS PRACY", "NIEZGODNY WYMIAR", "REZYGNACJA KLIENTA"];
    private static readonly string[] Requests = ["Dokument korygujący", "Wymiana na wolny od wad", "Wymiana na inny towar", "Dostarczenie braku", "Usunięcie wad"];
    private static readonly string[] Statuses = ["POTENCJALNY KLIENT", "KLIENT", "USŁUGI", "INSTYTUCJA"];
    private static readonly string[] Types = ["ODBIORCA", "DOSTAWCA", "ODBIORCA/DOSTAWCA", "INSTYTUCJA"];
    private static readonly string[] AgeBrackets = ["W terminie", "1–30 dni po terminie", "31–60 dni po terminie", "61–90 dni po terminie", "Ponad 90 dni po terminie"];

    public IReadOnlyList<MetricDefinition> Metrics { get; } = MetricCatalog.All;

    public Task<IReadOnlyList<DailyValue>> GetDailyAsync(string metricKey, MetricQuery query, CancellationToken ct = default)
    {
        if (HasNoCard(query))
        {
            return Task.FromResult<IReadOnlyList<DailyValue>>([]);
        }

        var scale = Scale(metricKey) * Share(query);
        var days = new List<DailyValue>(query.Range.Days);
        for (var day = query.Range.From; day <= query.Range.To; day = day.AddDays(1))
        {
            days.Add(new DailyValue(day, Math.Round((decimal)Day(metricKey, day) * scale, scale >= 1000 ? 2 : 0)));
        }

        return Task.FromResult<IReadOnlyList<DailyValue>>(days);
    }

    public Task<Ranking> GetRankingAsync(string metricKey, MetricQuery query, int top, CancellationToken ct = default)
    {
        if (HasNoCard(query))
        {
            return Task.FromResult(Ranking.Empty);
        }

        var metric = MetricCatalog.All.First(m => m.Key == metricKey);

        // Lists of documents / days – not shares of a period's total.
        switch (metricKey)
        {
            case MetricCatalog.ComplaintsOpenList:
                return Task.FromResult(new Ranking(Customers.Take(Math.Min(top, query.Contractor is null ? 8 : 2))
                    .Select((c, i) => new RankedValue($"{120 - (i * 7)}/26/S · {(query.Contractor is null ? c : "AGROMAX")}", 41 - (i * 5), "wystawiona 2026-10-0" + ((i % 9) + 1)))
                    .ToList()));
            case MetricCatalog.CrmNoContact:
                return Task.FromResult(new Ranking(Customers.Take(top).Select((c, i) => new RankedValue(c, 190 - (i * 14))).ToList()));
            case MetricCatalog.ReceivablesOverdueInvoices:
                return Task.FromResult(new Ranking(Customers.Take(Math.Min(top, query.Contractor is null ? 12 : 3))
                    .Select((c, i) => new RankedValue(
                        $"FS-{4210 - (i * 37)}/26 · {(query.Contractor is null ? c : "AGROMAX")} · {12 + (i * 9)} dni po terminie",
                        Math.Round(18_400m / (i + 1) * Share(query), 2), "termin płatności 2026-10-1" + (i % 9)))
                    .ToList()));
        }

        (string Label, string? Detail)[] names = metricKey switch
        {
            MetricCatalog.SalesTopProducts or MetricCatalog.ComplaintsTopProducts or MetricCatalog.ComplaintsTopReturnedProducts =>
                Products.Select(p => (p.Code, (string?)p.Name)).ToArray(),
            MetricCatalog.B2BTopProducts => Products.Select(p => (p.Code, (string?)null)).ToArray(),
            MetricCatalog.SalesByCountry => Plain(Countries),
            MetricCatalog.SalesBySalesperson or MetricCatalog.CrmByEmployee or MetricCatalog.CrmOverdueTasksByEmployee => Plain(Employees),
            MetricCatalog.CrmByTopic => Plain(Topics),
            MetricCatalog.CrmNewByStatus => Plain(Statuses),
            MetricCatalog.CrmNewByType => Plain(Types),
            MetricCatalog.ComplaintsTopReasons => Plain(Reasons),
            MetricCatalog.ComplaintsRequests => Plain(Requests),
            MetricCatalog.B2BTopSearches or MetricCatalog.B2BTopEmptySearches => Plain(Searches),
            MetricCatalog.ReceivablesAging => Plain(AgeBrackets),
            _ => Plain(Customers),
        };

        // Each name gets a stable share of the total – longer periods give larger numbers; "as of now" is one month's worth.
        var total = (metric.AsOfNow ? 30 : query.Range.Days) * Scale(metricKey) * Share(query);
        var rows = names
            .Select((n, i) => new RankedValue(n.Label, Math.Round(total * (decimal)(Noise(metricKey, i) + 0.3) / (i + 2), metric.Unit == MetricUnit.Money ? 2 : 0), n.Detail))
            .Where(r => r.Value > 0)
            .ToList();
        var all = rows.Sum(r => r.Value);
        var shown = metric.KeepOrder ? rows : rows.OrderByDescending(r => r.Value).Take(top).ToList();

        return Task.FromResult(new Ranking(shown, all));
    }

    public Task<decimal> GetSnapshotAsync(string metricKey, MetricQuery query, CancellationToken ct = default) =>
        Task.FromResult(HasNoCard(query) ? 0m : Math.Round(Scale(metricKey) * Share(query), metricKey.StartsWith("receivables.", StringComparison.Ordinal) ? 2 : 0));

    public Task<ContractorLink> FindAsync(int crmId, string shortName, string? nip, CancellationToken ct = default) =>
        // One of the demo contractors has no ERP card – shows the "not found in the ERP" state.
        Task.FromResult(shortName.StartsWith("GOSP.", StringComparison.OrdinalIgnoreCase)
            ? new ContractorLink(new ContractorRef(crmId, []), [])
            : new ContractorLink(new ContractorRef(crmId, [1000 + crmId]), [shortName]));

    public async Task<ContractorTurnover?> GetAsync(
        ClaimsPrincipal user, ContractorLink contractor, IReadOnlyList<DayRange> periods, CancellationToken ct = default)
    {
        if (!await access.CanAccessAsync(user, DataScopes.Sales, ct))
        {
            throw new AnalyticsException("Nie masz dostępu do danych o sprzedaży.", accessDenied: true);
        }

        if (!contractor.Contractor.HasErpCard)
        {
            return null;
        }

        // Every contractor has its own level of turnover.
        var name = contractor.ErpAcronyms[0];
        var size = 4000m + (decimal)(Noise(name, 0) * 60000);

        var result = new List<TurnoverPeriod>();
        foreach (var period in periods)
        {
            var months = new List<MonthlyTurnover>();
            var documents = 0;
            for (var month = new DateOnly(period.From.Year, period.From.Month, 1); month <= period.To; month = month.AddMonths(1))
            {
                var monthEnd = month.AddMonths(1).AddDays(-1);
                var from = month < period.From ? period.From : month;
                var to = monthEnd > period.To ? period.To : monthEnd;
                var share = (decimal)(to.DayNumber - from.DayNumber + 1) / DateTime.DaysInMonth(month.Year, month.Month);
                var net = Math.Round(size * share * (decimal)(Season(month) * (0.6 + Noise(name, month.DayNumber))), 2);
                months.Add(new MonthlyTurnover(month.Year, month.Month, net));
                documents += (int)Math.Ceiling(net / 1800m);
            }

            result.Add(new TurnoverPeriod(period, months.Sum(m => m.Net), documents, months));
        }

        return new ContractorTurnover(contractor.ErpAcronyms, result);
    }

    private static (string, string?)[] Plain(string[] names) => names.Select(n => (n, (string?)null)).ToArray();

    private static bool HasNoCard(MetricQuery query) => query.Contractor is { HasErpCard: false };

    /// <summary>One contractor is a small, stable part of the company's numbers.</summary>
    private static decimal Share(MetricQuery query) =>
        query.Contractor is { } contractor ? 0.012m + ((decimal)Noise("share", contractor.CrmId) * 0.03m) : 1m;

    /// <summary>Typical value of one working day (snapshots: the value itself).</summary>
    private static decimal Scale(string metricKey) => metricKey switch
    {
        MetricCatalog.SalesNet or MetricCatalog.SalesTopCustomers or MetricCatalog.SalesTopProducts or MetricCatalog.SalesByCountry
            or MetricCatalog.SalesBySalesperson => 185_000m,
        MetricCatalog.SalesDocuments => 240,
        MetricCatalog.SalesOrders => 210,
        MetricCatalog.CrmCorrespondence or MetricCatalog.CrmByEmployee or MetricCatalog.CrmByTopic or MetricCatalog.CrmTopContractors => 260,
        MetricCatalog.CrmEmails => 190,
        MetricCatalog.CrmEmailsIn => 120,
        MetricCatalog.CrmEmailsOut => 70,
        MetricCatalog.CrmCalls => 45,
        MetricCatalog.CrmMeetings => 14,
        MetricCatalog.CrmTasks => 22,
        MetricCatalog.CrmNewContractors or MetricCatalog.CrmNewByStatus or MetricCatalog.CrmNewByType => 4,
        MetricCatalog.CrmOverdueTasks => 86,
        MetricCatalog.CrmOverdueTasksByEmployee => 3,
        MetricCatalog.ComplaintsQuality or MetricCatalog.ComplaintsTopProducts or MetricCatalog.ComplaintsTopReasons => 5,
        MetricCatalog.ComplaintsDelivery => 3,
        MetricCatalog.ComplaintsReturns or MetricCatalog.ComplaintsTopCustomers or MetricCatalog.ComplaintsTopReturnedProducts
            or MetricCatalog.ComplaintsRequests => 12,
        MetricCatalog.ComplaintsOpen => 38,
        MetricCatalog.ComplaintsOpenReturns => 61,
        MetricCatalog.ReceivablesTotal => 4_750_000m,
        MetricCatalog.ReceivablesOverdue => 2_890_000m,
        MetricCatalog.ReceivablesAging or MetricCatalog.ReceivablesTopDebtors => 96_000m,
        MetricCatalog.B2BLogins or MetricCatalog.B2BTopCustomers => 1200,
        MetricCatalog.B2BSearches or MetricCatalog.B2BTopSearches => 4800,
        MetricCatalog.B2BEmptySearches or MetricCatalog.B2BTopEmptySearches => 520,
        MetricCatalog.B2BPageViews or MetricCatalog.B2BTopProducts => 9500,
        MetricCatalog.B2BOrders => 190,
        _ => 60,
    };

    /// <summary>A day's multiplier around 1: season of the year × slow growth × weekday × noise.</summary>
    private static double Day(string metricKey, DateOnly day)
    {
        var weekday = day.DayOfWeek switch
        {
            DayOfWeek.Saturday => 0.12,
            DayOfWeek.Sunday => 0.03,
            DayOfWeek.Monday => 1.15,
            _ => 1.0,
        };

        var growth = 1 + ((day.DayNumber - new DateOnly(2024, 1, 1).DayNumber) / 365.0 * 0.07);
        return Season(day) * growth * weekday * (0.75 + (Noise(metricKey, day.DayNumber) * 0.5));
    }

    /// <summary>Agricultural parts sell best before and during the harvest.</summary>
    private static double Season(DateOnly day) => 1 + (0.35 * Math.Sin((day.DayOfYear - 100) / 365.0 * 2 * Math.PI));

    /// <summary>Stable pseudo-random number in [0, 1) for a (name, number) pair – no Random, so it never changes.</summary>
    private static double Noise(string name, int n)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in name)
            {
                hash = (hash ^ c) * 16777619u;
            }

            hash = (hash ^ (uint)n) * 16777619u;
            hash ^= hash >> 15;
            hash *= 2246822519u;
            hash ^= hash >> 13;
            return (hash % 10000) / 10000.0;
        }
    }
}

/// <summary>Demo caretakers: every contractor gets a made-up account manager and representative; every fifth one none (GoNet's is shown).</summary>
internal sealed class DemoCaretakers(Core.Crm.IContractorService contractors) : Core.Crm.IContractorCaretakerService
{
    private static readonly string[] Managers = ["Jan Kowalski", "Anna Nowak"];
    private static readonly string[] Representatives = ["Piotr Wiśniewski", "Ewa Zielińska", "Marek Wójcik"];

    public async Task<IReadOnlyList<string>> GetNamesAsync(CancellationToken ct = default) =>
        (await AllAsync(ct)).Values.SelectMany(c => c.AccountManagers.Concat(c.SalesRepresentatives))
            .Distinct().Order(StringComparer.CurrentCultureIgnoreCase).ToList();

    public async Task<IReadOnlySet<int>> FindContractorsAsync(IReadOnlyCollection<string> names, CancellationToken ct = default)
    {
        var wanted = names.Select(Core.Crm.PersonNames.Normalize).ToHashSet();
        return (await AllAsync(ct))
            .Where(c => c.Value.AccountManagers.Concat(c.Value.SalesRepresentatives).Any(n => wanted.Contains(Core.Crm.PersonNames.Normalize(n))))
            .Select(c => c.Key).ToHashSet();
    }

    private async Task<Dictionary<int, Core.Crm.ContractorCaretakers>> AllAsync(CancellationToken ct)
    {
        var all = await contractors.SearchAsync(new Core.Crm.ContractorQuery(null, null, 0, 10_000), ct);
        var assignments = await GetAsync(all.Items.Select(c => new Core.Crm.ContractorKey(c.Id, c.ShortName, c.Nip)).ToList(), ct);
        return all.Items.ToDictionary(c => c.Id, c => Core.Crm.ContractorCaretakers.From(assignments.GetValueOrDefault(c.Id) ?? [], c.ManagerName));
    }

    public Task<IReadOnlyDictionary<int, IReadOnlyList<Core.Crm.CaretakerAssignment>>> GetAsync(
        IReadOnlyList<Core.Crm.ContractorKey> contractors, CancellationToken ct = default)
    {
        IReadOnlyDictionary<int, IReadOnlyList<Core.Crm.CaretakerAssignment>> result = contractors
            .Where(c => c.CrmId % 5 != 0)
            .DistinctBy(c => c.CrmId)
            .ToDictionary(c => c.CrmId, c => (IReadOnlyList<Core.Crm.CaretakerAssignment>)
            [
                new(Managers[c.CrmId % Managers.Length], true, Core.Crm.CaretakerSource.Region),
                new(Representatives[c.CrmId % Representatives.Length], false, Core.Crm.CaretakerSource.Region),
            ]);
        return Task.FromResult(result);
    }
}

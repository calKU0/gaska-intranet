using System.Security.Claims;
using Gaska.Intranet.Core.Analytics;
using Gaska.Intranet.Core.Auditing;
using Gaska.Intranet.Core.Modules;
using Gaska.Intranet.Core.Organization;
using Gaska.Intranet.Core.Security;
using Gaska.Intranet.Infrastructure;
using Gaska.Intranet.Infrastructure.Access;
using Gaska.Intranet.Infrastructure.Analytics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Tests;

public sealed class PeriodsTests
{
    private static readonly DateOnly Today = new(2026, 11, 13);

    [Theory]
    [InlineData(PeriodPreset.Last7Days, "2026-11-07", "2026-11-13")]
    [InlineData(PeriodPreset.ThisMonth, "2026-11-01", "2026-11-13")]
    [InlineData(PeriodPreset.LastMonth, "2026-10-01", "2026-10-31")]
    [InlineData(PeriodPreset.ThisQuarter, "2026-10-01", "2026-11-13")]
    [InlineData(PeriodPreset.LastQuarter, "2026-07-01", "2026-09-30")]
    [InlineData(PeriodPreset.ThisYear, "2026-01-01", "2026-11-13")]
    [InlineData(PeriodPreset.LastYear, "2025-01-01", "2025-12-31")]
    [InlineData(PeriodPreset.Last12Months, "2025-12-01", "2026-11-13")]
    public void Presets_resolve_to_dates(PeriodPreset preset, string from, string to) =>
        Assert.Equal(new DayRange(DateOnly.Parse(from), DateOnly.Parse(to)), Periods.Resolve(preset, Today));

    [Fact]
    public void Custom_range_with_swapped_dates_is_put_in_order() =>
        Assert.Equal(new DayRange(new DateOnly(2026, 1, 5), new DateOnly(2026, 2, 1)),
            Periods.Resolve(PeriodPreset.Custom, Today, new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 5)));

    [Fact]
    public void Previous_period_of_a_year_to_date_is_the_same_span_a_year_earlier() =>
        Assert.Equal(new DayRange(new DateOnly(2025, 1, 1), new DateOnly(2025, 11, 13)),
            Periods.Previous(Periods.Resolve(PeriodPreset.ThisYear, Today), PeriodPreset.ThisYear));

    [Fact]
    public void Previous_period_of_a_whole_month_is_the_whole_month_before()
    {
        // March (31 days) → February (28 days), not 31 days back.
        var march = new DayRange(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        Assert.Equal(new DayRange(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)), Periods.Previous(march, PeriodPreset.LastMonth));
    }

    [Fact]
    public void Previous_period_of_a_rolling_range_is_the_days_just_before() =>
        Assert.Equal(new DayRange(new DateOnly(2026, 10, 31), new DateOnly(2026, 11, 6)),
            Periods.Previous(Periods.Resolve(PeriodPreset.Last7Days, Today), PeriodPreset.Last7Days));

    [Fact]
    public void Weeks_start_on_monday() =>
        Assert.Equal(new DateOnly(2026, 11, 9), Periods.BucketStart(Today, Granularity.Week)); // 13.11.2026 is a Friday

    [Fact]
    public void Buckets_cover_the_whole_range_including_empty_ones()
    {
        var buckets = Periods.Buckets(new DayRange(new DateOnly(2025, 11, 20), new DateOnly(2026, 2, 3)), Granularity.Month);

        Assert.Equal(["lis 25", "gru 25", "sty 26", "lut 26"], buckets.Select(b => b.Label));
    }

    [Theory]
    [InlineData("2026-01-01", "2026-12-31", "2026")]
    [InlineData("2026-11-01", "2026-11-30", "listopad 2026")]
    [InlineData("2026-10-01", "2026-12-31", "Q4 2026")]
    [InlineData("2026-01-01", "2026-11-13", "1.01.2026 – 13.11.2026")]
    public void Ranges_are_described_the_way_people_say_them(string from, string to, string expected) =>
        Assert.Equal(expected, Periods.Describe(new DayRange(DateOnly.Parse(from), DateOnly.Parse(to))));
}

public sealed class AnalyticsServiceTests : IDisposable
{
    private const string SalesGroup = "S-1-5-21-500";
    private static readonly DayRange January = new(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

    private readonly TestDb _db = new();
    private readonly ModuleAccessService _access;
    private readonly FakeSource _source = new();
    private readonly AnalyticsService _service;

    public AnalyticsServiceTests()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        _access = new ModuleAccessService(_db, new Catalog(), cache, new NullAudit(),
            Options.Create(new AccessPolicyOptions()), TimeProvider.System);
        _service = new AnalyticsService([_source], _access, cache);
    }

    [Fact]
    public async Task Metric_is_hidden_and_refused_without_access_to_its_data_scope()
    {
        var warehouse = Employee("S-1-5-21-3");

        Assert.Empty(await _service.GetAvailableAsync(warehouse));
        Assert.False(await _service.CanViewAsync(warehouse, MetricCatalog.SalesNet));
        var denied = await Assert.ThrowsAsync<AnalyticsException>(() =>
            _service.GetSeriesAsync(warehouse, MetricCatalog.SalesNet, new MetricQuery(January), Granularity.Month));
        Assert.True(denied.AccessDenied);
        Assert.Equal(0, _source.Calls);
    }

    [Fact]
    public async Task Group_with_access_to_the_scope_sees_only_that_scope()
    {
        await _access.GrantAsync(DataScopes.Sales, new DirectoryPrincipal(SalesGroup, "Handlowcy", "Handlowcy", PrincipalKind.Group), "admin");
        var salesPerson = Employee("S-1-5-21-2", SalesGroup);

        var available = await _service.GetAvailableAsync(salesPerson);

        Assert.Equal([MetricCatalog.SalesNet, MetricCatalog.SalesTopCustomers], available.Select(m => m.Key));
        Assert.False(await _service.CanViewAsync(salesPerson, MetricCatalog.ComplaintsOpen));
        Assert.False(await _service.CanViewAsync(salesPerson, MetricCatalog.CrmEmails));
    }

    [Fact]
    public async Task Series_is_grouped_into_buckets_with_empty_ones_as_zero()
    {
        var admin = Employee("S-1-5-21-1", admin: true);
        var range = new DayRange(new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31));

        var points = await _service.GetSeriesAsync(admin, MetricCatalog.SalesNet, new MetricQuery(range), Granularity.Month);

        // The fake source returns 100 on 10 January and 50 + 25 in March – nothing in February.
        Assert.Equal([100m, 0m, 75m], points.Select(p => p.Value));
        Assert.Equal(["sty 26", "lut 26", "mar 26"], points.Select(p => p.Label));
    }

    [Fact]
    public async Task Results_are_cached_per_metric_and_range()
    {
        var admin = Employee("S-1-5-21-1", admin: true);

        await _service.GetSeriesAsync(admin, MetricCatalog.SalesNet, new MetricQuery(January), Granularity.Day);
        await _service.GetSeriesAsync(admin, MetricCatalog.SalesNet, new MetricQuery(January), Granularity.Week);

        Assert.Equal(1, _source.Calls);
    }

    [Fact]
    public async Task Contractor_numbers_are_cached_apart_from_the_company_totals()
    {
        var admin = Employee("S-1-5-21-1", admin: true);
        var contractor = new ContractorRef(7, [1007]);

        await _service.GetSeriesAsync(admin, MetricCatalog.SalesNet, new MetricQuery(January), Granularity.Day);
        await _service.GetSeriesAsync(admin, MetricCatalog.SalesNet, new MetricQuery(January, contractor), Granularity.Day);
        await _service.GetSeriesAsync(admin, MetricCatalog.SalesNet, new MetricQuery(January, contractor), Granularity.Month);

        Assert.Equal(2, _source.Calls);
        Assert.Equal(contractor, _source.LastQuery?.Contractor);
    }

    [Fact]
    public async Task Company_wide_ranking_cannot_be_asked_for_one_contractor() =>
        await Assert.ThrowsAsync<ArgumentException>(() => _service.GetRankingAsync(
            Employee("S-1-5-21-1", admin: true), MetricCatalog.SalesTopCustomers, new MetricQuery(January, new ContractorRef(7, [1007])), 5));

    [Fact]
    public async Task State_as_of_now_is_read_once_whatever_the_period()
    {
        var admin = Employee("S-1-5-21-1", admin: true);

        var first = await _service.GetSnapshotAsync(admin, MetricCatalog.ComplaintsOpen, new MetricQuery(January));
        var second = await _service.GetSnapshotAsync(admin, MetricCatalog.ComplaintsOpen,
            new MetricQuery(new DayRange(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31))));

        Assert.Equal(12m, first);
        Assert.Equal(first, second);
        Assert.Equal(1, _source.Calls);
    }

    [Fact]
    public async Task Data_scopes_are_not_listed_as_pages() =>
        Assert.DoesNotContain(await _access.GetAccessibleModulesAsync(Employee("S-1-5-21-1", admin: true)), m => m.Kind == ModuleKind.Data);

    public void Dispose() => _db.Dispose();

    private static ClaimsPrincipal Employee(string sid, string? groupSid = null, bool admin = false) =>
        new(IntranetClaims.CreateIdentity(new DirectoryUser
        {
            AccountName = "user" + sid[^1],
            Sid = sid,
            GroupSids = groupSid is null ? [] : [groupSid],
            IsAdministrator = admin,
        }, "test", DateTimeOffset.UtcNow));

    private sealed class FakeSource : IMetricSource
    {
        public int Calls { get; private set; }

        public MetricQuery? LastQuery { get; private set; }

        public IReadOnlyList<MetricDefinition> Metrics { get; } = MetricCatalog.All
            .Where(m => m.Key is MetricCatalog.SalesNet or MetricCatalog.SalesTopCustomers or MetricCatalog.CrmEmails or MetricCatalog.ComplaintsOpen)
            .ToList();

        public Task<IReadOnlyList<DailyValue>> GetDailyAsync(string metricKey, MetricQuery query, CancellationToken ct = default)
        {
            Calls++;
            LastQuery = query;
            return Task.FromResult<IReadOnlyList<DailyValue>>(
            [
                new(new DateOnly(2026, 1, 10), 100),
                new(new DateOnly(2026, 3, 2), 50),
                new(new DateOnly(2026, 3, 30), 25),
            ]);
        }

        public Task<Ranking> GetRankingAsync(string metricKey, MetricQuery query, int top, CancellationToken ct = default) =>
            Task.FromResult(new Ranking([new("A", 2), new("B", 1)], 5));

        public Task<decimal> GetSnapshotAsync(string metricKey, MetricQuery query, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(12m);
        }
    }

    private sealed class Catalog : IModuleCatalog
    {
        public IReadOnlyList<ModuleDefinition> All { get; } =
        [
            new() { Key = "page", Title = "Strona", Route = "/p", DefaultAccess = ModuleAccessMode.AllEmployees },
            new() { Key = DataScopes.Sales, Kind = ModuleKind.Data, Title = "Sprzedaż", DefaultAccess = ModuleAccessMode.Restricted },
            new() { Key = DataScopes.Crm, Kind = ModuleKind.Data, Title = "CRM", DefaultAccess = ModuleAccessMode.Restricted },
            new() { Key = DataScopes.Complaints, Kind = ModuleKind.Data, Title = "Reklamacje", DefaultAccess = ModuleAccessMode.Restricted },
        ];

        public ModuleDefinition? Find(string key) => All.FirstOrDefault(m => m.Key == key);
    }

    private sealed class NullAudit : IAuditLog
    {
        public Task WriteAsync(string eventType, string? accountName, string? details = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AuditEntry>> GetRecentAsync(int take = 500, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditEntry>>([]);
    }
}

public sealed class DashboardServiceTests : IDisposable
{
    private const string Anna = "S-1-5-21-10";
    private const string Jan = "S-1-5-21-11";

    private readonly TestDb _db = new();
    private readonly DashboardService _service;

    public DashboardServiceTests() => _service = new DashboardService(_db, TimeProvider.System);

    [Fact]
    public async Task Dashboard_from_a_template_keeps_the_order_of_its_charts()
    {
        var template = MetricCatalog.Templates[0];

        await _service.CreateAsync(Anna, "anna", " Sprzedaż ", template.Widgets);

        var dashboard = Assert.Single(await _service.GetAsync(Anna));
        Assert.Equal("Sprzedaż", dashboard.Name);
        Assert.Equal(template.Widgets.Select(w => (w.Kind, w.MetricKey, w.Title)), dashboard.Widgets.Select(w => (w.Kind, w.MetricKey, w.Title)));
        Assert.Equal(WidgetKind.Heading, dashboard.Widgets[0].Kind);
        Assert.NotNull(dashboard.Widgets[0].Description);
    }

    [Fact]
    public async Task Dashboards_are_private_to_their_owner()
    {
        var annas = await _service.CreateAsync(Anna, "anna", "Moja", [new WidgetInput { MetricKey = MetricCatalog.SalesNet }]);
        var widgetId = annas.Widgets[0].Id;

        Assert.Empty(await _service.GetAsync(Jan));

        // Someone else's ids change nothing.
        await _service.RenameAsync(Jan, annas.Id, "Przejęta");
        await _service.RemoveWidgetAsync(Jan, widgetId);
        await _service.UpdateWidgetAsync(Jan, widgetId, new WidgetInput { MetricKey = MetricCatalog.CrmEmails });
        await _service.DeleteAsync(Jan, annas.Id);
        await Assert.ThrowsAsync<DashboardException>(() =>
            _service.AddWidgetAsync(Jan, annas.Id, new WidgetInput { MetricKey = MetricCatalog.CrmEmails }));

        var dashboard = Assert.Single(await _service.GetAsync(Anna));
        Assert.Equal("Moja", dashboard.Name);
        Assert.Equal(MetricCatalog.SalesNet, Assert.Single(dashboard.Widgets).MetricKey);
    }

    [Fact]
    public async Task Chart_can_be_moved_earlier_and_later()
    {
        var dashboard = await _service.CreateAsync(Anna, "anna", "Strona",
        [
            new WidgetInput { MetricKey = "a" }, new WidgetInput { MetricKey = "b" }, new WidgetInput { MetricKey = "c" },
        ]);

        await _service.MoveWidgetAsync(Anna, dashboard.Widgets[2].Id, -1);
        await _service.MoveWidgetAsync(Anna, dashboard.Widgets[0].Id, -1); // already first – stays

        Assert.Equal(["a", "c", "b"], (await _service.GetAsync(Anna))[0].Widgets.Select(w => w.MetricKey));
    }

    [Fact]
    public async Task Dropped_chart_takes_its_new_place_and_unlisted_ones_follow()
    {
        var dashboard = await _service.CreateAsync(Anna, "anna", "Strona",
        [
            new WidgetInput { MetricKey = "a" }, new WidgetInput { MetricKey = "b" }, new WidgetInput { MetricKey = "c" }, new WidgetInput { MetricKey = "d" },
        ]);
        var ids = dashboard.Widgets.Select(w => w.Id).ToList();
        var other = await _service.CreateAsync(Jan, "jan", "Cudza", [new WidgetInput { MetricKey = "x" }]);

        // "d" dragged to the front; "c" is not in the list (added in another tab), a foreign id is ignored.
        await _service.ReorderWidgetsAsync(Anna, dashboard.Id, [ids[3], ids[0], other.Widgets[0].Id, ids[1]]);
        await _service.ReorderWidgetsAsync(Jan, dashboard.Id, [ids[0], ids[1], ids[2], ids[3]]); // not the owner – no effect

        Assert.Equal(["d", "a", "b", "c"], (await _service.GetAsync(Anna))[0].Widgets.Select(w => w.MetricKey));
    }

    [Fact]
    public async Task Group_heading_keeps_its_text_and_spans_the_page()
    {
        var dashboard = await _service.CreateAsync(Anna, "anna", "Strona");

        await _service.AddWidgetAsync(Anna, dashboard.Id, new WidgetInput
        {
            Kind = WidgetKind.Heading, Title = " Najwięksi klienci ", Description = "Top 5", Width = WidgetWidth.Small, MetricKey = "ignored", Color = 99,
        });

        var heading = (await _service.GetAsync(Anna))[0].Widgets[0];
        Assert.Equal((WidgetKind.Heading, "Najwięksi klienci", "Top 5", WidgetWidth.Full, string.Empty, 8),
            (heading.Kind, heading.Title, heading.Description, heading.Width, heading.MetricKey, heading.Color));
    }

    [Fact]
    public async Task Custom_dates_are_kept_only_for_a_custom_period()
    {
        var dashboard = await _service.CreateAsync(Anna, "anna", "Strona");
        var widget = await _service.AddWidgetAsync(Anna, dashboard.Id, new WidgetInput
        {
            MetricKey = MetricCatalog.SalesNet, Period = PeriodPreset.Custom, CustomFrom = new DateOnly(2026, 1, 1), CustomTo = new DateOnly(2026, 3, 31),
        });

        await _service.UpdateWidgetAsync(Anna, widget.Id, new WidgetInput
        {
            MetricKey = MetricCatalog.SalesNet, Period = PeriodPreset.ThisYear, CustomFrom = new DateOnly(2026, 1, 1), Title = "  ",
        });

        var saved = (await _service.GetAsync(Anna))[0].Widgets[0];
        Assert.Equal(PeriodPreset.ThisYear, saved.Period);
        Assert.Null(saved.CustomFrom);
        Assert.Null(saved.Title);
    }

    [Fact]
    public async Task Number_of_dashboards_and_charts_is_limited()
    {
        for (var i = 0; i < IDashboardService.MaxDashboards; i++)
        {
            await _service.CreateAsync(Anna, "anna", $"Strona {i}");
        }

        await Assert.ThrowsAsync<DashboardException>(() => _service.CreateAsync(Anna, "anna", "Za dużo"));
        await Assert.ThrowsAsync<DashboardException>(() => _service.CreateAsync(Jan, "jan", "   "));

        var full = await _service.CreateAsync(Jan, "jan", "Pełna",
            Enumerable.Range(0, IDashboardService.MaxWidgets + 5).Select(i => new WidgetInput { MetricKey = $"m{i}" }).ToList());
        Assert.Equal(IDashboardService.MaxWidgets, full.Widgets.Count);
        await Assert.ThrowsAsync<DashboardException>(() => _service.AddWidgetAsync(Jan, full.Id, new WidgetInput { MetricKey = "x" }));
    }

    public void Dispose() => _db.Dispose();
}

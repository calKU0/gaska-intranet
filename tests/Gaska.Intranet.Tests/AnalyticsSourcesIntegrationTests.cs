using Gaska.Intranet.Core.Analytics;
using Gaska.Intranet.Infrastructure.Analytics;
using Gaska.Intranet.Infrastructure.GoNet;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;
using Options = Microsoft.Extensions.Options.Options;

namespace Gaska.Intranet.Tests;

/// <summary>
/// Runs every dashboard metric against the real ERP, GoNet and B2B databases – read-only queries, nothing is written.
/// Checks that the SQL is valid for the company's databases, for the whole company and narrowed to one contractor, and
/// prints the totals (never asserted – they change daily). Runs only with src/Gaska.Intranet.Web/appsettings.Local.json
/// present; sources that are not configured are skipped.
/// </summary>
[Trait("Category", "Analytics")]
public sealed class AnalyticsSourcesIntegrationTests(ITestOutputHelper output)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);
    private static readonly DayRange Last90Days = new(Today.AddDays(-89), Today);

    [Fact]
    public async Task Erp_metrics_can_be_read()
    {
        if (LocalConfiguration() is not { } configuration || ErpOptions.ConnectionStrings.All(n => string.IsNullOrWhiteSpace(configuration.GetConnectionString(n))))
        {
            output.WriteLine("ERP connection not configured – test skipped.");
            return;
        }

        var erp = new ErpOptions();
        configuration.GetSection(ErpOptions.SectionName).Bind(erp);
        var source = new ErpMetricSource(configuration, Options.Create(erp), TimeProvider.System, NullLogger<ErpMetricSource>.Instance);

        await ReadAllAsync(source, contractor: null);
        await ReadAllAsync(source, await BusiestContractorAsync(source, configuration));
    }

    [Fact]
    public async Task GoNet_metrics_can_be_read()
    {
        if (LocalConfiguration() is not { } configuration || string.IsNullOrWhiteSpace(configuration.GetConnectionString(GoNetOptions.ConnectionStringName)))
        {
            output.WriteLine("GoNet connection not configured – test skipped.");
            return;
        }

        var goNet = new GoNetOptions();
        configuration.GetSection(GoNetOptions.SectionName).Bind(goNet);
        var source = new GoNetMetricSource(
            new GoNetDatabase(configuration, NullLogger<GoNetDatabase>.Instance), Options.Create(goNet), NullLogger<GoNetMetricSource>.Instance);

        await ReadAllAsync(source, contractor: null);

        // Any contractor will do for checking the narrowed queries – GoNet's first ids are the oldest customers.
        await ReadAllAsync(source, new ContractorRef(2, []));
    }

    [Fact]
    public async Task B2B_metrics_can_be_read()
    {
        if (LocalConfiguration() is not { } configuration || string.IsNullOrWhiteSpace(configuration.GetConnectionString(B2BMetricSource.ConnectionStringName)))
        {
            output.WriteLine("B2B connection not configured – test skipped.");
            return;
        }

        var source = new B2BMetricSource(configuration, NullLogger<B2BMetricSource>.Instance);
        await ReadAllAsync(source, contractor: null);

        // The most active B2B customer is found by its acronym in the ERP – the platform keys its data by the ERP card id.
        var top = await source.GetRankingAsync(MetricCatalog.B2BTopCustomers, new MetricQuery(Last90Days), 1);
        if (top.Rows.Count > 0 && ErpOptions.ConnectionStrings.Any(n => !string.IsNullOrWhiteSpace(configuration.GetConnectionString(n))))
        {
            var links = new ErpContractorLinks(configuration, new MemoryCache(new MemoryCacheOptions()), NullLogger<ErpContractorLinks>.Instance);
            var link = await links.FindAsync(0, top.Rows[0].Label, null);
            if (link.Contractor.HasErpCard)
            {
                await ReadAllAsync(source, link.Contractor);
            }
        }
    }

    /// <summary>The contractor with the highest sales in the period – so the narrowed queries return real rows.</summary>
    private static async Task<ContractorRef?> BusiestContractorAsync(ErpMetricSource source, IConfiguration configuration)
    {
        var top = await source.GetRankingAsync(MetricCatalog.SalesTopCustomers, new MetricQuery(Last90Days), 1);
        if (top.Rows.Count == 0)
        {
            return null;
        }

        var links = new ErpContractorLinks(configuration, new MemoryCache(new MemoryCacheOptions()), NullLogger<ErpContractorLinks>.Instance);
        var link = await links.FindAsync(0, top.Rows[0].Label, null);
        Assert.True(link.Contractor.HasErpCard);
        return link.Contractor;
    }

    private async Task ReadAllAsync(IMetricSource source, ContractorRef? contractor)
    {
        output.WriteLine(contractor is null ? "— whole company —" : "— one contractor —");
        var query = new MetricQuery(Last90Days, contractor);

        foreach (var metric in source.Metrics.Where(m => contractor is null || m.PerContractor))
        {
            try
            {
                switch (metric.Shape)
                {
                    case MetricShape.Series:
                        var days = await source.GetDailyAsync(metric.Key, query);
                        Assert.All(days, d => Assert.True(Last90Days.Contains(d.Day), $"{metric.Key}: {d.Day} outside the range"));
                        output.WriteLine($"{metric.Key,-34} {days.Count,3} days, total {days.Sum(d => d.Value):N0}");
                        break;
                    case MetricShape.Ranking:
                        var ranking = await source.GetRankingAsync(metric.Key, query, 5);
                        Assert.True(ranking.Rows.Count <= 5 || metric.KeepOrder);
                        Assert.All(ranking.Rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Label)));
                        if (ranking.Total is { } total)
                        {
                            Assert.True(total >= ranking.Rows.Sum(r => r.Value) - 0.01m, $"{metric.Key}: total below the sum of its rows");
                        }

                        output.WriteLine($"{metric.Key,-34} {ranking.Rows.Count} rows, top value {ranking.Rows.FirstOrDefault()?.Value:N0}, total {ranking.Total:N0}");
                        break;
                    default:
                        output.WriteLine($"{metric.Key,-34} now: {await source.GetSnapshotAsync(metric.Key, query):N0}");
                        break;
                }
            }
            catch (AnalyticsException ex) when (ex.InnerException is null)
            {
                // A setting of this metric is missing in appsettings.Local.json – reported, not a failure of the query.
                output.WriteLine($"{metric.Key,-34} not configured: {ex.Message}");
            }
        }
    }

    private static IConfiguration? LocalConfiguration()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Gaska.Intranet.Web", "appsettings.Local.json"));
        return File.Exists(path) ? new ConfigurationBuilder().AddJsonFile(path).Build() : null;
    }
}

using Gaska.Intranet.Infrastructure.Analytics;
using Gaska.Intranet.Core.Crm;
using Gaska.Intranet.Infrastructure.GoNet;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;
using Options = Microsoft.Extensions.Options.Options;

namespace Gaska.Intranet.Tests;

/// <summary>
/// Runs the contractor list filters against the real GoNet database – read-only. Checks that every filter narrows the
/// list and that combined filters narrow it further. Runs only with a GoNet connection in
/// src/Gaska.Intranet.Web/appsettings.Local.json; otherwise it is a no-op.
/// </summary>
[Trait("Category", "GoNet")]
public sealed class ContractorFilterIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Filters_narrow_the_contractor_list()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Gaska.Intranet.Web", "appsettings.Local.json"));
        if (!File.Exists(path))
        {
            output.WriteLine("appsettings.Local.json not found – test skipped.");
            return;
        }

        var configuration = new ConfigurationBuilder().AddJsonFile(path).Build();
        var goNet = new GoNetOptions();
        configuration.GetSection(GoNetOptions.SectionName).Bind(goNet);
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString(GoNetOptions.ConnectionStringName)) || goNet.MissingIds().Any())
        {
            output.WriteLine("GoNet not configured – test skipped.");
            return;
        }

        var options = Options.Create(goNet);
        var db = new GoNetDatabase(configuration, NullLogger<GoNetDatabase>.Instance);
        var dictionary = new GoNetDictionary(db, new MemoryCache(new MemoryCacheOptions()), options);
        var contractors = new GoNetContractorService(db, dictionary, options, new NoGeocoder(), TimeProvider.System);

        var choices = await dictionary.GetContractorFilterOptionsAsync();
        output.WriteLine($"countries {choices.Countries.Count}, voivodeships {choices.Voivodeships.Count}, types {choices.Types.Count}, " +
            $"statuses {choices.Statuses.Count}");
        Assert.NotEmpty(choices.Countries);
        Assert.NotEmpty(choices.Types);
        Assert.NotEmpty(choices.Statuses);

        var all = await CountAsync(null);
        var firstCountry = await CountAsync(new ContractorFilter { CountryIds = [choices.Countries[0].Id] });
        var twoCountries = await CountAsync(new ContractorFilter { CountryIds = [choices.Countries[0].Id, choices.Countries[1].Id] });
        var status = await CountAsync(new ContractorFilter { StatusIds = [choices.Statuses[0].Id] });
        var countryAndStatus = await CountAsync(new ContractorFilter { CountryIds = [choices.Countries[0].Id], StatusIds = [choices.Statuses[0].Id] });
        var type = await CountAsync(new ContractorFilter { TypeIds = [choices.Types[0].Id] });
        var voivodeship = choices.Voivodeships.Count > 0 ? await CountAsync(new ContractorFilter { VoivodeshipIds = [choices.Voivodeships[0].Id] }) : 0;

        output.WriteLine($"all {all}; most common country {firstCountry}; two countries {twoCountries}; status {status}; " +
            $"country+status {countryAndStatus}; type {type}; voivodeship {voivodeship}");

        Assert.InRange(firstCountry, 1, all);
        Assert.True(twoCountries > firstCountry, "A second country adds contractors (values of one filter are alternatives).");
        Assert.InRange(countryAndStatus, 0, Math.Min(firstCountry, status));
        Assert.InRange(type, 1, all);

        // Caretakers from the ERP: the list limited to one person's contractors has exactly them, paged.
        var caretakers = new ErpContractorCaretakerService(configuration, contractors, new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System, NullLogger<ErpContractorCaretakerService>.Instance);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var names = await caretakers.GetNamesAsync();
        var indexTime = watch.ElapsedMilliseconds;
        Assert.NotEmpty(names);
        var ids = await caretakers.FindContractorsAsync([names[0].ToLowerInvariant()]);
        watch.Restart();
        var page = await contractors.SearchAsync(new ContractorQuery(null, ids, 0, 20));
        output.WriteLine($"caretakers {names.Count} (index {indexTime} ms); contractors of the first one {ids.Count} (page {watch.ElapsedMilliseconds} ms)");
        Assert.InRange(ids.Count, 1, all);
        Assert.Equal(ids.Count, page.TotalCount);
        Assert.All(page.Items, c => Assert.Contains(c.Id, ids));
        Assert.Empty((await contractors.SearchAsync(new ContractorQuery(null, new HashSet<int>(), 0, 20))).Items);

        async Task<int> CountAsync(ContractorFilter? filter) =>
            (await contractors.SearchAsync(new ContractorQuery(null, null, 0, 1, Filter: filter))).TotalCount;
    }

    private sealed class NoGeocoder : IGeocoder
    {
        public Task<GeoPoint?> GeocodeAsync(string address, CancellationToken ct = default) => Task.FromResult<GeoPoint?>(null);
    }
}

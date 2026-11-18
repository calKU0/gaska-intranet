using Gaska.Intranet.Core.Analytics;
using Gaska.Intranet.Core.Crm;
using Gaska.Intranet.Infrastructure.Analytics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;
using Options = Microsoft.Extensions.Options.Options;

namespace Gaska.Intranet.Tests;

public sealed class CaretakerTests
{
    private static CaretakerAssignment Region(string name, bool main) => new(name, main, CaretakerSource.Region);

    private static CaretakerAssignment Card(string name) => new(name, false, CaretakerSource.Contractor);

    [Fact]
    public void Main_region_caretaker_is_the_account_manager_and_the_others_are_representatives()
    {
        var caretakers = ContractorCaretakers.From(
            [Region("Anna A", true), Region("Piotr B", false), Region("Ewa C", false), Card("Jan D")], "GoNet Person");

        Assert.Equal(["Anna A"], caretakers.AccountManagers);
        Assert.Equal("Piotr B, Ewa C", caretakers.SalesRepresentative);
        Assert.False(caretakers.AccountManagerFromGoNet);
    }

    [Fact]
    public void Without_a_region_the_caretaker_of_the_card_is_the_account_manager()
    {
        var caretakers = ContractorCaretakers.From([Card("Jan D"), Card("Jan D")], "GoNet Person");

        Assert.Equal(["Jan D"], caretakers.AccountManagers);
        Assert.Empty(caretakers.SalesRepresentatives);
    }

    [Fact]
    public void Without_any_erp_account_manager_gonet_manager_is_used()
    {
        var caretakers = ContractorCaretakers.From([Region("Piotr B", false)], " GoNet Person ");

        Assert.Equal("GoNet Person", caretakers.AccountManager);
        Assert.True(caretakers.AccountManagerFromGoNet);
        Assert.Equal("Piotr B", caretakers.SalesRepresentative);
    }

    [Fact]
    public void A_supplier_is_looked_after_by_the_caretaker_of_its_card_even_with_a_region()
    {
        var caretakers = ContractorCaretakers.From(
            [new("Anna A", true, CaretakerSource.Region, Supplier: true), new("Jan D", false, CaretakerSource.Contractor, Supplier: true)],
            "GoNet Person");

        Assert.Equal(["Jan D"], caretakers.AccountManagers);
    }

    [Fact]
    public void A_supplier_without_a_card_caretaker_falls_back_to_the_region_then_gonet()
    {
        Assert.Equal(["Anna A"], ContractorCaretakers.From([new("Anna A", true, CaretakerSource.Region, Supplier: true)], "GoNet Person").AccountManagers);
        Assert.True(ContractorCaretakers.From([new("Piotr B", false, CaretakerSource.Region, Supplier: true)], "GoNet Person").AccountManagerFromGoNet);
    }

    [Fact]
    public void A_customer_prefers_the_region_over_its_card()
    {
        var caretakers = ContractorCaretakers.From([Region("Anna A", true), Card("Jan D")], null);

        Assert.Equal(["Anna A"], caretakers.AccountManagers);
    }

    [Fact]
    public void Nothing_known_gives_no_names()
    {
        var caretakers = ContractorCaretakers.From([], null);

        Assert.Null(caretakers.AccountManager);
        Assert.Null(caretakers.SalesRepresentative);
    }
}

/// <summary>Reads caretakers of the biggest customers from the real ERP (read-only); runs only with appsettings.Local.json.</summary>
[Trait("Category", "Analytics")]
public sealed class CaretakerIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Caretakers_of_customers_can_be_read()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Gaska.Intranet.Web", "appsettings.Local.json"));
        if (!File.Exists(path))
        {
            output.WriteLine("No local configuration – test skipped.");
            return;
        }

        var configuration = new ConfigurationBuilder().AddJsonFile(path).Build();
        if (ErpOptions.ConnectionStrings.All(n => string.IsNullOrWhiteSpace(configuration.GetConnectionString(n))))
        {
            output.WriteLine("ERP connection not configured – test skipped.");
            return;
        }

        // The biggest customers of the last year – their acronyms are the CRM short names.
        var source = new ErpMetricSource(configuration, Options.Create(new ErpOptions()), TimeProvider.System, NullLogger<ErpMetricSource>.Instance);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var top = await source.GetRankingAsync(MetricCatalog.SalesTopCustomers, new MetricQuery(new DayRange(today.AddYears(-1), today)), 30);
        var keys = top.Rows.Select((r, i) => new ContractorKey(i + 1, r.Label, null)).ToList();

        var service = new ErpContractorCaretakerService(
            // GoNet is needed only by the caretaker filter (tested in ContractorFilterIntegrationTests).
            configuration, goNet: null!, new MemoryCache(new MemoryCacheOptions()), TimeProvider.System, NullLogger<ErpContractorCaretakerService>.Instance);
        var found = await service.GetAsync(keys);

        var caretakers = keys.Select(k => ContractorCaretakers.From(found.GetValueOrDefault(k.CrmId) ?? [], null)).ToList();
        output.WriteLine($"{keys.Count} customers: {caretakers.Count(c => c.AccountManagers.Count > 0)} with an account manager, " +
                         $"{caretakers.Count(c => c.SalesRepresentatives.Count > 0)} with a representative.");

        Assert.NotEmpty(keys);
        Assert.Contains(caretakers, c => c.AccountManagers.Count > 0);

        // A supplier card with both a regional and its own caretaker gets its own one.
        var supplier = await service.GetAsync([new ContractorKey(-1, SupplierWithBoth(configuration), null)]);
        var supplierCaretakers = ContractorCaretakers.From(supplier.GetValueOrDefault(-1) ?? [], null);
        var cardNames = (supplier.GetValueOrDefault(-1) ?? []).Where(a => a.Source == CaretakerSource.Contractor).Select(a => a.Name).ToList();
        Assert.NotEmpty(cardNames);
        Assert.All(supplierCaretakers.AccountManagers, n => Assert.Contains(n, cardNames));

        // The second call is served from the cache – the same answer.
        Assert.Equal(found.Count, (await service.GetAsync(keys)).Count);
    }

    /// <summary>The acronym of some supplier card (Knt_Typ 8) that has a region and a caretaker of its own.</summary>
    private static string SupplierWithBoth(IConfiguration configuration)
    {
        var connection = ErpOptions.ConnectionStrings.Select(configuration.GetConnectionString).First(c => !string.IsNullOrWhiteSpace(c));
        using var sql = new Microsoft.Data.SqlClient.SqlConnection(connection);
        sql.Open();
        using var command = sql.CreateCommand();
        command.CommandText = """
            SELECT TOP 1 k.Knt_Akronim FROM CDN.KntKarty k
            WHERE k.Knt_Typ = 8
              AND EXISTS (SELECT 1 FROM CDN.KntRejony r JOIN CDN.KntOpiekun o ON o.KtO_KntTyp = 948 AND o.KtO_KntNumer = r.KnR_Rejon
                          WHERE r.KnR_KntTyp = 32 AND r.KnR_KntNumer = k.Knt_GIDNumer AND o.KtO_Glowny = 1)
              AND EXISTS (SELECT 1 FROM CDN.KntOpiekun o WHERE o.KtO_KntTyp = 32 AND o.KtO_KntNumer = k.Knt_GIDNumer AND o.KtO_DataDo >= 0)
              AND (SELECT COUNT(*) FROM CDN.KntKarty x WHERE x.Knt_Akronim = k.Knt_Akronim) = 1
            ORDER BY k.Knt_GIDNumer
            """;
        return (string)command.ExecuteScalar()!;
    }
}

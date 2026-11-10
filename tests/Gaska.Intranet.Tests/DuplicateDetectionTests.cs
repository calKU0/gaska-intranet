using Gaska.Intranet.Core.Crm;
using Gaska.Intranet.Infrastructure.GoNet;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;
using Options = Microsoft.Extensions.Options.Options;

namespace Gaska.Intranet.Tests;

public class CompanyNamesTests
{
    [Theory]
    [InlineData("NOVA GROUP, S.R.O.", "NOVA GROUP s.r.o.")]
    [InlineData("AGRO-SERWIS Sp. z o.o.", "Agro Serwis spółka z ograniczoną odpowiedzialnością")]
    [InlineData("Ve.Ra Group S.r.l.", "VE.RA GROUP SRL")]
    [InlineData("Tulip International BV", "TULIP INTERNATIONAL B.V.")]
    [InlineData("Veritas S.A.", "VERITAS SA")]
    [InlineData("Nova Group", "Nova Group Praha s.r.o.")]
    public void Same_company_regardless_of_legal_form_and_punctuation(string a, string b) =>
        Assert.True(CompanyNames.Similar(a, b));

    [Theory]
    [InlineData("AGRO Sp. z o.o.", "AGROMAX Sp. z o.o.")]
    [InlineData("Veritas S.A.", "Veritum S.A.")]
    [InlineData("ABC", "ABC Transport Kowalski")] // too short to be distinctive
    public void Different_companies(string a, string b) =>
        Assert.False(CompanyNames.Similar(a, b));

    [Theory]
    [InlineData("http://www.klcz.cz", "klcz.cz")]
    [InlineData("www.klcz.cz/kontakt", "klcz.cz")]
    [InlineData("linzhao@klcz.cz", "klcz.cz")]
    [InlineData("jan.kowalski@gmail.com", null)]
    [InlineData("", null)]
    public void Domain_of_website_or_email(string value, string? expected) =>
        Assert.Equal(expected, CompanyNames.Domain(value));
}

/// <summary>Duplicate search against the real GoNet database – read-only (SELECT only).</summary>
[Trait("Category", "GoNet")]
public sealed class GoNetDuplicateDetectionTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Existing_contractor_is_found_by_each_signal()
    {
        if (GoNetRollbackIntegrationTests.ConnectionString() is not { } connectionString)
        {
            output.WriteLine("GoNet connection string not configured – test skipped.");
            return;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:GoNet"] = connectionString })
            .Build();
        var options = Options.Create(new GoNetOptions());
        var db = new GoNetDatabase(configuration, NullLogger<GoNetDatabase>.Instance);
        var contractors = new GoNetContractorService(db, new GoNetDictionary(db, new MemoryCache(new MemoryCacheOptions()), options),
            options, new GoNetRollbackIntegrationTests.FixedGeocoder(new GeoPoint(0, 0)), TimeProvider.System);

        // Any existing contractor with a valid Polish NIP, e-mail and website – no real company is named in the test.
        ContractorDetails? sample = null;
        await using (var connection = await db.OpenAsync(CancellationToken.None))
        await using (var command = GoNetDatabase.Command(connection, null, """
            SELECT FIRST 50 k.ID FROM KONTRAHENT k
            WHERE k.USUNIETY = 0 AND COALESCE(k.ARC, 0) = 0 AND CHAR_LENGTH(k.NIP) >= 10
              AND k.EMAIL CONTAINING '@' AND CHAR_LENGTH(k.WWW) > 4
            ORDER BY k.ID DESC
            """))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (sample is null && await reader.ReadAsync())
            {
                var details = await contractors.GetAsync(reader.GetInt32(0));
                if (details is not null && Nip.Normalize(details.Nip) is not null)
                {
                    sample = details;
                }
            }
        }

        if (sample is null)
        {
            output.WriteLine("No contractor with a NIP, e-mail and website – test skipped.");
            return;
        }

        // Each signal alone must find it.
        var queries = new Dictionary<string, ContractorMatchQuery>
        {
            ["NIP"] = new(sample.Nip, [], [], null),
            ["name"] = new(null, [], [], sample.FullName),
            ["website"] = new(null, [], [], null, sample.Www),
            ["e-mail"] = new(null, [sample.Email!], [], null),
        };

        foreach (var (signal, query) in queries)
        {
            var matches = await contractors.FindMatchesAsync(query);
            output.WriteLine($"{signal}: {matches.Count} match(es)");
            Assert.Contains(matches, m => m.Id == sample.Id);
        }
    }
}

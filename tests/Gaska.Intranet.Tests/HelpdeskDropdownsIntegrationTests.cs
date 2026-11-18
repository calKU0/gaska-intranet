using Gaska.Intranet.Infrastructure.Helpdesk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;
using Options = Microsoft.Extensions.Options.Options;

namespace Gaska.Intranet.Tests;

/// <summary>
/// Reads the ticket categories and locations from the real GLPI as the service account – the lists employees with a
/// Self-Service profile get in the new ticket form. Read-only. Runs only when the service account is configured
/// ("Glpi:Attachments" in src/Gaska.Intranet.Web/appsettings.Local.json); otherwise it is a no-op.
/// </summary>
[Trait("Category", "Glpi")]
public sealed class HelpdeskDropdownsIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Service_account_can_read_categories_and_locations()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Gaska.Intranet.Web", "appsettings.Local.json"));
        var glpi = new GlpiOptions();
        if (File.Exists(path))
        {
            new ConfigurationBuilder().AddJsonFile(path).Build().GetSection(GlpiOptions.SectionName).Bind(glpi);
        }

        if (string.IsNullOrWhiteSpace(glpi.BaseUrl) || !glpi.Attachments.IsConfigured)
        {
            output.WriteLine("GLPI service account not configured – test skipped.");
            return;
        }

        var uploader = new GlpiLegacyUploader(new HttpClients(), Options.Create(glpi), NullLogger<GlpiLegacyUploader>.Instance);

        var categories = await uploader.GetCategoriesAsync(CancellationToken.None);
        var locations = await uploader.GetLocationsAsync(CancellationToken.None);

        output.WriteLine($"categories: {categories.Count}, offered in the simplified form: {categories.Count(c => c.HelpdeskVisible)} " +
            $"(problems {categories.Count(c => c.HelpdeskVisible && c.Incident)}, requests {categories.Count(c => c.HelpdeskVisible && c.Request)})");
        output.WriteLine($"locations: {locations.Count}");
        Assert.NotEmpty(categories);
    }

    private sealed class HttpClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(30) };
    }
}

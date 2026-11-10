using Microsoft.Extensions.Configuration.Json;

namespace Gaska.Intranet.Web.Configuration;

public static class LocalSettings
{
    public const string FileName = "appsettings.Local.json";

    /// <summary>
    /// Adds <c>appsettings.Local.json</c> – the secrets file, ignored by git (template: appsettings.Local.example.json).
    /// Inserted right after appsettings.{Environment}.json, so it overrides the committed files,
    /// while environment variables and command-line arguments still take precedence.
    /// </summary>
    public static IConfigurationBuilder AddLocalSettings(this IConfigurationManager configuration, IHostEnvironment environment)
    {
        var builder = (IConfigurationBuilder)configuration;
        var environmentFile = $"appsettings.{environment.EnvironmentName}.json";

        var index = builder.Sources
            .Select((source, i) => (source, i))
            .LastOrDefault(x => x.source is JsonConfigurationSource json &&
                (json.Path == environmentFile || json.Path == "appsettings.json"))
            .i;

        var local = new JsonConfigurationSource { Path = FileName, Optional = true, ReloadOnChange = true };
        builder.Sources.Insert(index + 1, local);
        return builder;
    }
}

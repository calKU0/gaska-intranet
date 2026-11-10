using System.Net;
using Gaska.Intranet.Infrastructure;
using Gaska.Intranet.Web.Components;
using Gaska.Intranet.Web.Configuration;
using Gaska.Intranet.Web.Security;
using Microsoft.AspNetCore.HttpOverrides;
using MudBlazor.Services;
using Serilog;

// Until the configuration is read, errors go to the console AND to logs\startup-*.log next to the application –
// on IIS a crash at startup (missing appsettings.Local.json, wrong connection string …) is otherwise only "HTTP 500".
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(AppContext.BaseDirectory, "logs", "startup-.log"), rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 10)
    .CreateBootstrapLogger();

try
{
    // Polish UI: "40 675", "6.11.2026", "pon." – independent of the server's Windows language.
    // (Data sent to databases and APIs uses explicit or invariant formats.)
    var polish = System.Globalization.CultureInfo.GetCultureInfo("pl-PL");
    System.Globalization.CultureInfo.DefaultThreadCurrentCulture = polish;
    System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = polish;

    var builder = WebApplication.CreateBuilder(args);
    builder.Configuration.AddLocalSettings(builder.Environment);

    builder.Host.UseSerilog((context, services, logger) => logger
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    // Do not reveal the server name (IIS: see web.config).
    builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

    builder.Services.AddIntranetInfrastructure(builder.Configuration, builder.Environment);
    builder.Services.AddIntranetSecurity(builder.Configuration);
    builder.Services.AddMudServices(o =>
    {
        o.SnackbarConfiguration.PositionClass = MudBlazor.Defaults.Classes.Position.BottomRight;
        o.SnackbarConfiguration.VisibleStateDuration = 4000;
    });
    // Dropdowns decide once, when opening, whether to open up or down. The default (re-check on every scroll/resize)
    // made selects on phones flip back and forth endlessly: the browser bars and the on-screen keyboard keep resizing
    // the viewport, every flip moves the page, and that triggers the next flip.
    builder.Services.Configure<MudBlazor.PopoverOptions>(o => o.OverflowBehavior = MudBlazor.OverflowBehavior.FlipOnOpen);
    builder.Services.AddScoped<Gaska.Intranet.Web.Crm.CrmSession>();
    builder.Services.Configure<List<Gaska.Intranet.Web.Links.QuickLink>>(builder.Configuration.GetSection("QuickLinks"));
    builder.Services.AddRazorComponents().AddInteractiveServerComponents();

    var app = builder.Build();

    if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
    {
        await app.Services.MigrateIntranetDatabaseAsync();
    }

    // Behind a reverse proxy the real client address is in X-Forwarded-For – trust only the configured proxies.
    var trustedProxies = app.Configuration.GetSection("Security:TrustedProxies").Get<string[]>() ?? [];
    if (trustedProxies.Length > 0)
    {
        var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
        foreach (var proxy in trustedProxies)
        {
            forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
        }

        app.UseForwardedHeaders(forwarded);
    }

    app.UseMiddleware<SecurityHeadersMiddleware>();

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/blad", createScopeForErrors: true);
        app.UseHsts();
    }

    app.UseStatusCodePagesWithReExecute("/nie-znaleziono", createScopeForStatusCodePages: true);
    app.UseHttpsRedirection();
    app.UseSerilogRequestLogging();

    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseMiddleware<NetworkClaimMiddleware>();
    app.UseAuthorization();
    app.UseAntiforgery();

    app.MapStaticAssets().AllowAnonymous();
    app.MapAccountEndpoints();

    // User manual as PDF – for signed-in users only (static files are public, so it is not in wwwroot).
    app.MapGet(Gaska.Intranet.Web.Components.Pages.Help.ManualContent.PdfRoute, () =>
            File.Exists(Gaska.Intranet.Web.Components.Pages.Help.ManualContent.PdfPath)
                ? Results.File(Gaska.Intranet.Web.Components.Pages.Help.ManualContent.PdfPath, "application/pdf",
                    Gaska.Intranet.Web.Components.Pages.Help.ManualContent.PdfFileName)
                : Results.NotFound())
        .RequireAuthorization();
    Gaska.Intranet.Web.Components.Pages.Helpdesk.HelpdeskEndpoints.MapHelpdeskEndpoints(app);
    app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Aplikacja zakończyła działanie z powodu błędu");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

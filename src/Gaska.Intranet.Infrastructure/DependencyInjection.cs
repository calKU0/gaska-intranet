using Gaska.Intranet.Core.Access;
using Gaska.Intranet.Core.Crm;
using Gaska.Intranet.Infrastructure.BusinessCards;
using Gaska.Intranet.Infrastructure.GoNet;
using Gaska.Intranet.Core.Helpdesk;
using Gaska.Intranet.Infrastructure.Helpdesk;
using Gaska.Intranet.Core.Rcp;
using Gaska.Intranet.Infrastructure.Rcp;
using Gaska.Intranet.Core.Auditing;
using Gaska.Intranet.Core.Messaging;
using Gaska.Intranet.Core.Organization;
using Gaska.Intranet.Core.Security;
using Gaska.Intranet.Infrastructure.Access;
using Gaska.Intranet.Infrastructure.ActiveDirectory;
using Gaska.Intranet.Infrastructure.Auditing;
using Gaska.Intranet.Infrastructure.Messaging;
using Gaska.Intranet.Infrastructure.Persistence;
using Gaska.Intranet.Infrastructure.Security;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Gaska.Intranet.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddIntranetInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddOptions<ActiveDirectoryOptions>().Bind(configuration.GetSection(ActiveDirectoryOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<SmsOptions>().Bind(configuration.GetSection(SmsOptions.SectionName)).ValidateDataAnnotations()
            .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.Endpoint), "Sms:Endpoint is required when SMS sending is enabled.")
            .ValidateOnStart();
        services.AddOptions<SmtpOptions>().Bind(configuration.GetSection(SmtpOptions.SectionName));
        services.AddOptions<AccessPolicyOptions>().Bind(configuration.GetSection(AccessPolicyOptions.SectionName));
        services.AddOptions<TwoFactorOptions>().Bind(configuration.GetSection(TwoFactorOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();

        services.AddMemoryCache();
        services.AddHttpContextAccessor();
        services.TryAddTimeProvider();

        // Application database
        var connectionString = configuration.GetConnectionString("Intranet")
            ?? throw new InvalidOperationException("Brak ConnectionStrings:Intranet w konfiguracji.");
        services.AddDbContextFactory<IntranetDbContext>(o => o.UseSqlServer(connectionString, sql =>
        {
            sql.MigrationsHistoryTable("__EFMigrationsHistory", IntranetDbContext.Schema);
            sql.EnableRetryOnFailure();
        }));

        // Cookie encryption keys in the database – sessions survive restarts/redeploys and work across servers.
        var dataProtection = services.AddDataProtection()
            .SetApplicationName("Gaska.Intranet")
            .PersistKeysToDbContext<IntranetDbContext>();

        // Encrypt the key ring itself with Windows DPAPI, so a stolen database is not enough to decrypt cookies or
        // authenticator secrets. Machine scope: any account on THIS server can read the keys (IIS pool, an admin starting
        // the app from the console, a changed pool identity) – user scope broke the site as soon as the app was once
        // started under another account. Keys protected on one machine cannot be read on another, so enable it on the
        // production server only.
        if (configuration.GetValue<bool>("Security:ProtectKeysWithDpapi") && OperatingSystem.IsWindows())
        {
            dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
        }

        AddDirectory(services, configuration, environment);

        // Messaging
        services.AddHttpClient<ISmsSender, GaskaSmsGateway>((sp, client) =>
        {
            var sms = configuration.GetSection(SmsOptions.SectionName).Get<SmsOptions>() ?? new SmsOptions();
            client.Timeout = TimeSpan.FromSeconds(sms.TimeoutSeconds);
        });
        services.AddSingleton<IEmailSender, SmtpEmailSender>();

        // Two-factor authentication
        services.AddSingleton<ILoginCodeSender, SmsLoginCodeSender>();
        services.AddSingleton<ILoginCodeSender, EmailLoginCodeSender>();
        services.AddSingleton<ILoginCodeService, LoginCodeService>();
        services.AddSingleton<ILoginThrottle, LoginThrottle>();
        services.AddSingleton<ITrustedDeviceService, TrustedDeviceService>();
        services.AddSingleton<IAuthenticatorService, AuthenticatorService>();

        // Uprawnienia i audyt
        services.AddSingleton<IAuditLog, DbAuditLog>();
        services.AddSingleton<IModuleAccessService, ModuleAccessService>();

        AddCrm(services, configuration);

        // Time clock (Punktualnik via the XL SQL Server linked server)
        services.AddOptions<RcpOptions>().Bind(configuration.GetSection(RcpOptions.SectionName));
        services.AddSingleton<IRcpService, PunktualnikRcpService>();

        AddHelpdesk(services, configuration);
        AddDemo(services, configuration, environment);

        return services;
    }

    private static void AddDirectory(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var provider = configuration.GetValue(
            $"{ActiveDirectoryOptions.SectionName}:{nameof(ActiveDirectoryOptions.Provider)}",
            DirectoryProvider.ActiveDirectory);

        if (provider == DirectoryProvider.Fake)
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException("The fake directory (ActiveDirectory:Provider=Fake) is allowed only in the Development environment.");
            }

            services.AddSingleton<FakeDirectory>();
            services.AddSingleton<IDirectoryAuthenticator>(sp => sp.GetRequiredService<FakeDirectory>());
            services.AddSingleton<IEmployeeDirectory>(sp => sp.GetRequiredService<FakeDirectory>());
            services.AddSingleton<IPrincipalSearch>(sp => sp.GetRequiredService<FakeDirectory>());
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Integracja z Active Directory wymaga serwera Windows w domenie.");
        }

        AddActiveDirectory(services);
    }

    [SupportedOSPlatform("windows")]
    private static void AddActiveDirectory(IServiceCollection services)
    {
        services.AddSingleton<DirectoryConnection>();
        services.AddSingleton<ActiveDirectoryAccountService>();
        services.AddSingleton<IDirectoryAuthenticator>(sp => sp.GetRequiredService<ActiveDirectoryAccountService>());
        services.AddSingleton<IPrincipalSearch>(sp => sp.GetRequiredService<ActiveDirectoryAccountService>());
        services.AddSingleton<IEmployeeDirectory, ActiveDirectoryEmployeeDirectory>();
    }

    /// <summary>GoNet CRM (Firebird) and business card reading.</summary>
    private static void AddCrm(IServiceCollection services, IConfiguration configuration)
    {
        // The GoNet ids are specific to the target database (appsettings.Local.json) – fail at startup, not on the first save.
        var goNetConfigured = !string.IsNullOrWhiteSpace(configuration.GetConnectionString(GoNetOptions.ConnectionStringName));
        services.AddOptions<GoNetOptions>().Bind(configuration.GetSection(GoNetOptions.SectionName))
            .Validate(o => !goNetConfigured || !o.MissingIds().Any(),
                "GoNet ids missing in the \"GoNet\" configuration section (appsettings.Local.json).")
            .ValidateOnStart();
        services.AddOptions<BusinessCardOptions>().Bind(configuration.GetSection(BusinessCardOptions.SectionName));

        services.AddSingleton<GoNetDatabase>();
        services.AddSingleton<GoNetDictionary>();
        services.AddSingleton<ICrmDirectory>(sp => sp.GetRequiredService<GoNetDictionary>());
        services.AddSingleton<IContractorService, GoNetContractorService>();
        services.AddSingleton<ICrmTaskService, GoNetTaskService>();

        services.AddSingleton<TesseractBusinessCardReader>();
        // No client timeout: BusinessCardReader limits each call (LlmTimeoutSeconds) and falls back to OCR.
        services.AddHttpClient<OllamaCardReader>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddTransient<IBusinessCardReader, BusinessCardReader>();
        // Company registers: GUS (REGON) first, then the VAT white list.
        services.AddOptions<GusOptions>().Bind(configuration.GetSection(GusOptions.SectionName));
        services.AddHttpClient<GusBirRegistry>(client => client.Timeout = TimeSpan.FromSeconds(15));
        services.AddHttpClient<MfWhiteListRegistry>(client =>
        {
            client.BaseAddress = new Uri("https://wl-api.mf.gov.pl/");
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddTransient<ICompanyRegistry, CompanyRegistry>();

        // Address → coordinates (OpenStreetMap Nominatim requires an identifying User-Agent).
        services.AddHttpClient<IGeocoder, NominatimGeocoder>(client =>
        {
            client.BaseAddress = new Uri("https://nominatim.openstreetmap.org/");
            client.Timeout = TimeSpan.FromSeconds(8);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Gaska-Intranet/1.0 (+https://gaska.com.pl)");
        });
    }

    /// <summary>GLPI helpdesk – API v2 as the signed-in user (OAuth2 tokens obtained at intranet login).</summary>
    private static void AddHelpdesk(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<GlpiOptions>().Bind(configuration.GetSection(GlpiOptions.SectionName));
        services.AddHttpClient(GlpiConnector.HttpClientName, (sp, client) =>
        {
            // Per-request limits are set by the callers (reads vs. writes); this is only the outer safety net.
            var glpi = configuration.GetSection(GlpiOptions.SectionName).Get<GlpiOptions>() ?? new GlpiOptions();
            client.Timeout = TimeSpan.FromSeconds(Math.Max(glpi.TimeoutSeconds, glpi.WriteTimeoutSeconds) + 10);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });
        services.AddSingleton<GlpiConnector>();
        services.AddSingleton<GlpiLegacyUploader>();
        // With the fake directory (Development only – enforced in AddDirectory) the helpdesk is faked too:
        // fake users have no GLPI account, and local UI work must not touch real tickets.
        if (configuration.GetValue($"{ActiveDirectoryOptions.SectionName}:{nameof(ActiveDirectoryOptions.Provider)}",
                DirectoryProvider.ActiveDirectory) == DirectoryProvider.Fake)
        {
            services.AddSingleton<FakeHelpdesk>();
            services.AddSingleton<IHelpdeskConnector>(sp => sp.GetRequiredService<FakeHelpdesk>());
            services.AddSingleton<IHelpdeskService>(sp => sp.GetRequiredService<FakeHelpdesk>());
            return;
        }

        services.AddSingleton<IHelpdeskConnector>(sp => sp.GetRequiredService<GlpiConnector>());
        services.AddSingleton<IHelpdeskService, GlpiHelpdeskService>();
    }

    /// <summary>
    /// Demo:Enabled (Development only) – sample time clock and CRM instead of Punktualnik and GoNet: screenshots for the
    /// user manual without real customer data, UI work without the company systems. Registered last, so it wins.
    /// </summary>
    private static void AddDemo(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        if (!configuration.GetValue<bool>("Demo:Enabled"))
        {
            return;
        }

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException("Demo:Enabled (sample data instead of GoNet / Punktualnik) is allowed only in the Development environment.");
        }

        services.AddSingleton<Demo.DemoRcp>();
        services.AddSingleton<IRcpService>(sp => sp.GetRequiredService<Demo.DemoRcp>());
        services.AddSingleton<Demo.DemoCrm>();
        services.AddSingleton<ICrmDirectory>(sp => sp.GetRequiredService<Demo.DemoCrm>());
        services.AddSingleton<IContractorService>(sp => sp.GetRequiredService<Demo.DemoCrm>());
        services.AddSingleton<ICrmTaskService>(sp => sp.GetRequiredService<Demo.DemoCrm>());
    }

    private static void TryAddTimeProvider(this IServiceCollection services)
    {
        if (services.All(d => d.ServiceType != typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }

    /// <summary>Creates / updates the database schema (EF Core migrations).</summary>
    public static async Task MigrateIntranetDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<IntranetDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);
    }
}

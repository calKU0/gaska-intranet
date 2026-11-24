using Gaska.Intranet.Core.Analytics;
using Gaska.Intranet.Core.Cameras;
using Gaska.Intranet.Core.Modules;
using MudBlazor;

namespace Gaska.Intranet.Web.Modules;

/// <summary>Permanent module keys – stored in the database, do not change after deployment.</summary>
public static class ModuleKeys
{
    public const string Employees = "employees";
    public const string Contractors = "crm.contractors";
    public const string Rcp = "rcp";
    public const string Helpdesk = "helpdesk";
    public const string Dashboards = "dashboards";
    public const string Cameras = CameraLocations.ModuleKey;
    public const string AdminAccess = "admin.access";
    public const string AdminAudit = "admin.audit";
    public const string AdminDevices = "admin.devices";
    public const string AdminAuthenticators = "admin.authenticators";
}

/// <summary>
/// Application module registry. A new page = a new entry here + a page with the attribute
/// <c>[ModuleAuthorize(ModuleKeys.Xxx)]</c>. The menu and the permissions panel update automatically.
/// </summary>
public sealed class ModuleCatalog : IModuleCatalog
{
    public const string AdminSection = "Administracja";

    /// <summary>Heading of the data scopes in the permissions panel (they have no menu entries).</summary>
    public const string DataSection = "Dane";

    /// <summary>Heading of the camera locations in the permissions panel.</summary>
    public const string CamerasSection = "Kamery";

    private readonly Dictionary<string, ModuleDefinition> _byKey;

    public ModuleCatalog()
    {
        All =
        [
            new ModuleDefinition
            {
                Key = ModuleKeys.Employees,
                Title = "Pracownicy",
                Description = "Książka adresowa – telefony, numery wewnętrzne, działy.",
                Route = "/pracownicy",
                Icon = Icons.Material.Outlined.Contacts,
                Order = 10,
                DefaultAccess = ModuleAccessMode.AllEmployees,
            },
            new ModuleDefinition
            {
                Key = ModuleKeys.Rcp,
                Title = "RCP",
                Description = "Rejestracja czasu pracy – rozpoczęcie i zakończenie pracy, przerwy.",
                Route = "/rcp",
                Icon = Icons.Material.Outlined.Timer,
                Order = 15,
                DefaultAccess = ModuleAccessMode.AllEmployees,
            },
            new ModuleDefinition
            {
                Key = ModuleKeys.Helpdesk,
                Title = "Helpdesk IT",
                Description = "Zgłoszenia do działu IT – nowe zgłoszenie, Twoje i obserwowane, odpowiedzi.",
                Route = "/helpdesk",
                Icon = Icons.Material.Outlined.SupportAgent,
                Order = 17,
                DefaultAccess = ModuleAccessMode.AllEmployees,
            },
            new ModuleDefinition
            {
                Key = ModuleKeys.Contractors,
                Title = "Kontrahenci",
                Description = "Karty kontrahentów z GoNet – osoby, zadania, wizyty startowe, wizytówki.",
                Route = "/kontrahenci",
                Icon = Icons.Material.Outlined.Storefront,
                Order = 20,
                DefaultAccess = ModuleAccessMode.Restricted,
            },
            new ModuleDefinition
            {
                Key = ModuleKeys.Dashboards,
                Title = "Dashboardy",
                Description = "Własne strony z wykresami – sprzedaż, należności, klienci i ich aktywność, reklamacje.",
                Route = "/dashboardy",
                Icon = Icons.Material.Outlined.Dashboard,
                Order = 30,
                DefaultAccess = ModuleAccessMode.Restricted,
            },
            new ModuleDefinition
            {
                Key = ModuleKeys.Cameras,
                Title = "Kamery",
                Description = "Podgląd kamer na żywo i nagrania – biuro, magazyny, sklep, produkcja, pakowanie, teren.",
                Route = "/kamery",
                Icon = Icons.Material.Outlined.Videocam,
                Order = 25,
                DefaultAccess = ModuleAccessMode.Restricted,
            },

            // Data scopes – who may see a kind of data, wherever it is shown (contractor card, dashboards).
            new ModuleDefinition
            {
                Key = DataScopes.Sales,
                Kind = ModuleKind.Data,
                Title = "Sprzedaż i obroty",
                Description = "Obroty kontrahentów na karcie kontrahenta oraz wykresy sprzedaży w dashboardach (dane z ERP XL).",
                Icon = Icons.Material.Outlined.Payments,
                Section = DataSection,
                Order = 500,
                DefaultAccess = ModuleAccessMode.Restricted,
            },
            new ModuleDefinition
            {
                Key = DataScopes.Crm,
                Kind = ModuleKind.Data,
                Title = "Komunikacja z klientami",
                Description = "Korespondencja, telefony, spotkania, zadania i nowi klienci z GoNet – w dashboardach i na karcie kontrahenta.",
                Icon = Icons.Material.Outlined.Forum,
                Section = DataSection,
                Order = 510,
                DefaultAccess = ModuleAccessMode.Restricted,
            },
            new ModuleDefinition
            {
                Key = DataScopes.Complaints,
                Kind = ModuleKind.Data,
                Title = "Reklamacje i zwroty",
                Description = "Reklamacje jakościowe, reklamacje dostaw i zwroty z ERP XL – w dashboardach i na karcie kontrahenta.",
                Icon = Icons.Material.Outlined.AssignmentReturn,
                Section = DataSection,
                Order = 520,
                DefaultAccess = ModuleAccessMode.Restricted,
            },
            new ModuleDefinition
            {
                Key = DataScopes.Receivables,
                Kind = ModuleKind.Data,
                Title = "Należności",
                Description = "Niezapłacone i przeterminowane faktury sprzedaży, najwięksi dłużnicy (dane z ERP XL).",
                Icon = Icons.Material.Outlined.AccountBalanceWallet,
                Section = DataSection,
                Order = 525,
                DefaultAccess = ModuleAccessMode.Restricted,
            },
            new ModuleDefinition
            {
                Key = DataScopes.B2B,
                Kind = ModuleKind.Data,
                Title = "Aktywność B2B",
                Description = "Logowania, wyszukiwania, klikane towary i zamówienia z platformy B2B – w dashboardach i na karcie kontrahenta.",
                Icon = Icons.Material.Outlined.ShoppingCart,
                Section = DataSection,
                Order = 530,
                DefaultAccess = ModuleAccessMode.Restricted,
            },
            new ModuleDefinition
            {
                Key = ModuleKeys.AdminAccess,
                Title = "Uprawnienia",
                Description = "Kto ma dostęp do poszczególnych modułów.",
                Route = "/admin/uprawnienia",
                Icon = Icons.Material.Outlined.AdminPanelSettings,
                Section = AdminSection,
                Order = 900,
                DefaultAccess = ModuleAccessMode.AdministratorsOnly,
            },
            new ModuleDefinition
            {
                Key = ModuleKeys.AdminAuthenticators,
                Title = "Aplikacje 2FA",
                Description = "Kto ma skonfigurowaną aplikację (TOTP) – reset przy zgubionym telefonie.",
                Route = "/admin/aplikacje-uwierzytelniajace",
                Icon = Icons.Material.Outlined.PhonelinkLock,
                Section = AdminSection,
                Order = 903,
                DefaultAccess = ModuleAccessMode.AdministratorsOnly,
            },
            new ModuleDefinition
            {
                Key = ModuleKeys.AdminDevices,
                Title = "Zaufane urządzenia",
                Description = "Urządzenia logujące się bez kodu – możliwość unieważnienia.",
                Route = "/admin/urzadzenia",
                Icon = Icons.Material.Outlined.Devices,
                Section = AdminSection,
                Order = 905,
                DefaultAccess = ModuleAccessMode.AdministratorsOnly,
            },
            new ModuleDefinition
            {
                Key = ModuleKeys.AdminAudit,
                Title = "Dziennik zdarzeń",
                Description = "Logowania i zmiany uprawnień.",
                Route = "/admin/dziennik",
                Icon = Icons.Material.Outlined.History,
                Section = AdminSection,
                Order = 910,
                DefaultAccess = ModuleAccessMode.AdministratorsOnly,
            },
        ];

        // Camera locations – a tab of the "Kamery" page each; visible only together with the page permission.
        All = All.Concat(CameraLocations.All.Select((location, index) => new ModuleDefinition
        {
            Key = location.ModuleKey,
            Kind = ModuleKind.Data,
            Title = location.Title,
            Description = location.Description,
            Icon = Icons.Material.Outlined.Videocam,
            Section = CamerasSection,
            Order = 600 + index * 10,
            DefaultAccess = ModuleAccessMode.Restricted,
        })).OrderBy(m => m.Order).ToList();
        _byKey = All.ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ModuleDefinition> All { get; }

    public ModuleDefinition? Find(string key) => _byKey.GetValueOrDefault(key);
}

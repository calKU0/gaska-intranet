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

        All = All.OrderBy(m => m.Order).ToList();
        _byKey = All.ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ModuleDefinition> All { get; }

    public ModuleDefinition? Find(string key) => _byKey.GetValueOrDefault(key);
}

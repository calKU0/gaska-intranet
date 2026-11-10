namespace Gaska.Intranet.Core.Modules;

/// <summary>Module access mode used until an administrator changes it.</summary>
public enum ModuleAccessMode
{
    /// <summary>Every signed-in employee.</summary>
    AllEmployees = 1,

    /// <summary>Only users / groups selected in the admin panel.</summary>
    Restricted = 2,

    /// <summary>Application administrators only (cannot be changed in the panel).</summary>
    AdministratorsOnly = 3,
}

/// <summary>
/// Describes an application module (page / tab). Each module has a permanent key that
/// permissions in the database are attached to.
/// </summary>
public sealed record ModuleDefinition
{
    /// <summary>Permanent unique identifier – do not change after deployment (stored in the database).</summary>
    public required string Key { get; init; }

    public required string Title { get; init; }

    public string Description { get; init; } = string.Empty;

    /// <summary>URL of the module's start page, e.g. "/pracownicy".</summary>
    public required string Route { get; init; }

    /// <summary>Icon (SVG) – the icon set is defined by the UI layer.</summary>
    public string Icon { get; init; } = string.Empty;

    /// <summary>Order in the menu.</summary>
    public int Order { get; init; }

    /// <summary>Menu section (e.g. "Sprzedaż", "Administracja"); <c>null</c> = main section.</summary>
    public string? Section { get; init; }

    public ModuleAccessMode DefaultAccess { get; init; } = ModuleAccessMode.Restricted;
}

/// <summary>Registry of all application modules.</summary>
public interface IModuleCatalog
{
    IReadOnlyList<ModuleDefinition> All { get; }

    ModuleDefinition? Find(string key);
}

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

/// <summary>What a permission entry protects.</summary>
public enum ModuleKind
{
    /// <summary>A page with its own menu entry.</summary>
    Page = 0,

    /// <summary>
    /// A data scope (e.g. sales figures) shown inside other pages – no page and no menu entry of its own; the same
    /// access modes and AD rules apply.
    /// </summary>
    Data = 1,
}

/// <summary>
/// Describes an application module (page / tab) or a data scope. Each module has a permanent key that
/// permissions in the database are attached to.
/// </summary>
public sealed record ModuleDefinition
{
    /// <summary>Permanent unique identifier – do not change after deployment (stored in the database).</summary>
    public required string Key { get; init; }

    public required string Title { get; init; }

    public string Description { get; init; } = string.Empty;

    public ModuleKind Kind { get; init; } = ModuleKind.Page;

    /// <summary>URL of the module's start page, e.g. "/pracownicy" (empty for a data scope).</summary>
    public string Route { get; init; } = string.Empty;

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

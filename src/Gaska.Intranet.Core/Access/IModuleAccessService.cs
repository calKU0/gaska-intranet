using System.Security.Claims;
using Gaska.Intranet.Core.Modules;
using Gaska.Intranet.Core.Organization;

namespace Gaska.Intranet.Core.Access;

/// <summary>Module access configuration – view model for the admin panel.</summary>
public sealed record ModuleAccessOverview(
    ModuleDefinition Module,
    ModuleAccessMode EffectiveMode,
    bool IsOverridden,
    IReadOnlyList<ModuleAccessRule> Rules,
    bool InternalNetworkOnly = false);

/// <summary>Checks and manages module permissions.</summary>
public interface IModuleAccessService
{
    Task<bool> CanAccessAsync(ClaimsPrincipal user, string moduleKey, CancellationToken ct = default);

    Task<IReadOnlyList<ModuleDefinition>> GetAccessibleModulesAsync(ClaimsPrincipal user, CancellationToken ct = default);

    Task<IReadOnlyList<ModuleAccessOverview>> GetOverviewAsync(CancellationToken ct = default);

    Task SetAccessModeAsync(string moduleKey, ModuleAccessMode mode, string changedBy, CancellationToken ct = default);

    /// <summary>Restricts the module to the company network (or allows it from any IP address).</summary>
    Task SetInternalNetworkOnlyAsync(string moduleKey, bool internalOnly, string changedBy, CancellationToken ct = default);

    /// <summary>The user has permission to the module but it is closed for their network (shown on the "access denied" page).</summary>
    Task<bool> IsBlockedByNetworkAsync(ClaimsPrincipal user, string moduleKey, CancellationToken ct = default);

    Task<ModuleAccessRule> GrantAsync(string moduleKey, DirectoryPrincipal principal, string changedBy, CancellationToken ct = default);

    Task RevokeAsync(int ruleId, string changedBy, CancellationToken ct = default);
}

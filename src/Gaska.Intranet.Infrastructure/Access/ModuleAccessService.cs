using System.Security.Claims;
using Gaska.Intranet.Core.Access;
using Gaska.Intranet.Core.Auditing;
using Gaska.Intranet.Core.Modules;
using Gaska.Intranet.Core.Organization;
using Gaska.Intranet.Core.Security;
using Gaska.Intranet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Infrastructure.Access;

/// <summary>
/// Access rules:
/// 1. Application administrators see everything.
/// 2. "Administrators only" modules – administrators only (by default only from the company network).
/// 3. "All employees" modules – every signed-in user.
/// 4. "Restricted" modules – users whose SID, or the SID of any of their groups (including nested), has a rule.
/// Any configurable module can additionally be limited to the company network – for everyone, administrators included.
/// </summary>
internal sealed class ModuleAccessService(
    IDbContextFactory<IntranetDbContext> dbFactory,
    IModuleCatalog catalog,
    IMemoryCache cache,
    IAuditLog audit,
    IOptions<AccessPolicyOptions> policy,
    TimeProvider clock) : IModuleAccessService
{
    private const string CacheKey = "access:snapshot";

    public async Task<bool> CanAccessAsync(ClaimsPrincipal user, string moduleKey, CancellationToken ct = default)
    {
        var module = catalog.Find(moduleKey);
        return module is not null && CanAccess(user, module, await GetSnapshotAsync(ct));
    }

    public async Task<IReadOnlyList<ModuleDefinition>> GetAccessibleModulesAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(ct);
        return catalog.All.Where(m => CanAccess(user, m, snapshot)).ToList();
    }

    public async Task<IReadOnlyList<ModuleAccessOverview>> GetOverviewAsync(CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(ct);
        return catalog.All
            .Select(m => new ModuleAccessOverview(
                m,
                EffectiveMode(m, snapshot),
                snapshot.Modes.ContainsKey(m.Key),
                snapshot.Rules.TryGetValue(m.Key, out var rules) ? rules : [],
                snapshot.InternalOnly.Contains(m.Key)))
            .ToList();
    }

    public async Task SetAccessModeAsync(string moduleKey, ModuleAccessMode mode, string changedBy, CancellationToken ct = default)
    {
        var module = RequireConfigurable(moduleKey);
        if (mode == ModuleAccessMode.AdministratorsOnly)
        {
            throw new InvalidOperationException("The 'administrators only' mode cannot be set from the panel.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var setting = await db.ModuleSettings.FindAsync([module.Key], ct);
        if (setting is null)
        {
            setting = new ModuleSetting { ModuleKey = module.Key };
            db.ModuleSettings.Add(setting);
        }

        setting.AccessMode = mode;
        setting.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        setting.UpdatedBy = changedBy;
        await db.SaveChangesAsync(ct);

        cache.Remove(CacheKey);
        await audit.WriteAsync(AuditEvents.AccessModeChanged, changedBy, $"{module.Key}: {mode}", ct);
    }

    public async Task SetInternalNetworkOnlyAsync(string moduleKey, bool internalOnly, string changedBy, CancellationToken ct = default)
    {
        var module = RequireConfigurable(moduleKey);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var setting = await db.ModuleSettings.FindAsync([module.Key], ct);
        if (setting is null)
        {
            // Keep the module's current access mode – only the network restriction changes.
            setting = new ModuleSetting { ModuleKey = module.Key, AccessMode = module.DefaultAccess };
            db.ModuleSettings.Add(setting);
        }

        setting.InternalNetworkOnly = internalOnly;
        setting.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        setting.UpdatedBy = changedBy;
        await db.SaveChangesAsync(ct);

        cache.Remove(CacheKey);
        await audit.WriteAsync(AuditEvents.AccessNetworkChanged, changedBy,
            $"{module.Key}: {(internalOnly ? "internal network only" : "any network")}", ct);
    }

    public async Task<bool> IsBlockedByNetworkAsync(ClaimsPrincipal user, string moduleKey, CancellationToken ct = default)
    {
        var module = catalog.Find(moduleKey);
        if (module is null || user.Identity?.IsAuthenticated != true || user.IsOnInternalNetwork())
        {
            return false;
        }

        var snapshot = await GetSnapshotAsync(ct);
        return !CanAccess(user, module, snapshot) && CanAccess(user, module, snapshot, assumeInternalNetwork: true);
    }

    public async Task<ModuleAccessRule> GrantAsync(string moduleKey, DirectoryPrincipal principal, string changedBy, CancellationToken ct = default)
    {
        var module = RequireConfigurable(moduleKey);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.ModuleAccessRules
            .FirstOrDefaultAsync(r => r.ModuleKey == module.Key && r.PrincipalSid == principal.Sid, ct);
        if (existing is not null)
        {
            return existing;
        }

        var rule = new ModuleAccessRule
        {
            ModuleKey = module.Key,
            PrincipalKind = principal.Kind,
            PrincipalSid = principal.Sid,
            PrincipalName = principal.Kind == PrincipalKind.User
                ? $"{principal.DisplayName} ({principal.AccountName})"
                : principal.DisplayName,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
            CreatedBy = changedBy,
        };

        db.ModuleAccessRules.Add(rule);
        await db.SaveChangesAsync(ct);

        cache.Remove(CacheKey);
        await audit.WriteAsync(AuditEvents.AccessGranted, changedBy, $"{module.Key}: {rule.PrincipalName}", ct);
        return rule;
    }

    public async Task RevokeAsync(int ruleId, string changedBy, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rule = await db.ModuleAccessRules.FindAsync([ruleId], ct);
        if (rule is null)
        {
            return;
        }

        db.ModuleAccessRules.Remove(rule);
        await db.SaveChangesAsync(ct);

        cache.Remove(CacheKey);
        await audit.WriteAsync(AuditEvents.AccessRevoked, changedBy, $"{rule.ModuleKey}: {rule.PrincipalName}", ct);
    }

    private bool CanAccess(ClaimsPrincipal user, ModuleDefinition module, AccessSnapshot snapshot, bool assumeInternalNetwork = false)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var internalNetwork = assumeInternalNetwork || user.IsOnInternalNetwork();
        if (module.DefaultAccess == ModuleAccessMode.AdministratorsOnly)
        {
            return user.IsAdministrator() &&
                (!policy.Value.AdminOnlyFromInternalNetwork || internalNetwork);
        }

        if (snapshot.InternalOnly.Contains(module.Key) && !internalNetwork)
        {
            return false;
        }

        if (user.IsAdministrator())
        {
            return true;
        }

        switch (EffectiveMode(module, snapshot))
        {
            case ModuleAccessMode.AllEmployees:
                return true;
            case ModuleAccessMode.Restricted when snapshot.Rules.TryGetValue(module.Key, out var rules):
                var sids = user.GetSecurityIdentifiers();
                return rules.Any(r => sids.Contains(r.PrincipalSid));
            default:
                return false;
        }
    }

    private static ModuleAccessMode EffectiveMode(ModuleDefinition module, AccessSnapshot snapshot) =>
        module.DefaultAccess == ModuleAccessMode.AdministratorsOnly
            ? ModuleAccessMode.AdministratorsOnly
            : snapshot.Modes.GetValueOrDefault(module.Key, module.DefaultAccess);

    private ModuleDefinition RequireConfigurable(string moduleKey)
    {
        var module = catalog.Find(moduleKey) ?? throw new ArgumentException($"Unknown module '{moduleKey}'.", nameof(moduleKey));
        if (module.DefaultAccess == ModuleAccessMode.AdministratorsOnly)
        {
            throw new InvalidOperationException($"Module '{module.Title}' is available to administrators only.");
        }

        return module;
    }

    /// <summary>The whole permission configuration is small – it is cached in memory and invalidated on change.</summary>
    private async Task<AccessSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(CacheKey, out AccessSnapshot? snapshot) && snapshot is not null)
        {
            return snapshot;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await db.ModuleSettings.AsNoTracking().ToListAsync(ct);
        var modes = settings.ToDictionary(s => s.ModuleKey, s => s.AccessMode);
        var internalOnly = settings.Where(s => s.InternalNetworkOnly).Select(s => s.ModuleKey).ToHashSet();
        var rules = (await db.ModuleAccessRules.AsNoTracking().OrderBy(r => r.PrincipalName).ToListAsync(ct))
            .GroupBy(r => r.ModuleKey)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ModuleAccessRule>)g.ToList());

        snapshot = new AccessSnapshot(modes, rules, internalOnly);
        cache.Set(CacheKey, snapshot, TimeSpan.FromMinutes(5));
        return snapshot;
    }

    private sealed record AccessSnapshot(
        IReadOnlyDictionary<string, ModuleAccessMode> Modes,
        IReadOnlyDictionary<string, IReadOnlyList<ModuleAccessRule>> Rules,
        IReadOnlySet<string> InternalOnly);
}

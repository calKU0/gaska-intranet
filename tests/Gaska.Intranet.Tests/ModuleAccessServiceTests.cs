using System.Security.Claims;
using Gaska.Intranet.Core.Auditing;
using Gaska.Intranet.Core.Modules;
using Gaska.Intranet.Core.Organization;
using Gaska.Intranet.Core.Security;
using Gaska.Intranet.Infrastructure;
using Gaska.Intranet.Infrastructure.Access;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Tests;

public sealed class ModuleAccessServiceTests : IDisposable
{
    private const string SalesGroup = "S-1-5-21-500";
    private readonly TestDb _db = new();
    private readonly ModuleAccessService _service;

    public ModuleAccessServiceTests() =>
        _service = new ModuleAccessService(_db, new Catalog(), new MemoryCache(new MemoryCacheOptions()), new NullAudit(),
            Options.Create(new AccessPolicyOptions { AdminOnlyFromInternalNetwork = true }), TimeProvider.System);

    [Fact]
    public async Task Everyone_module_is_open_to_any_employee() =>
        Assert.True(await _service.CanAccessAsync(Employee("S-1-5-21-1"), "public"));

    [Fact]
    public async Task Anonymous_user_has_no_access() =>
        Assert.False(await _service.CanAccessAsync(new ClaimsPrincipal(new ClaimsIdentity()), "public"));

    [Fact]
    public async Task Restricted_module_requires_rule_for_user_or_group()
    {
        var salesPerson = Employee("S-1-5-21-2", SalesGroup);
        var warehouse = Employee("S-1-5-21-3");

        Assert.False(await _service.CanAccessAsync(salesPerson, "sales"));

        await _service.GrantAsync("sales", new DirectoryPrincipal(SalesGroup, "Dzial-Sprzedaz", "Dział Sprzedaż", PrincipalKind.Group), "admin");

        Assert.True(await _service.CanAccessAsync(salesPerson, "sales"));
        Assert.False(await _service.CanAccessAsync(warehouse, "sales"));

        await _service.GrantAsync("sales", new DirectoryPrincipal("S-1-5-21-3", "magazyn", "Magazynier", PrincipalKind.User), "admin");
        Assert.True(await _service.CanAccessAsync(warehouse, "sales"));
    }

    [Fact]
    public async Task Revoking_rule_removes_access()
    {
        var rule = await _service.GrantAsync("sales", new DirectoryPrincipal(SalesGroup, "g", "g", PrincipalKind.Group), "admin");
        await _service.RevokeAsync(rule.Id, "admin");

        Assert.False(await _service.CanAccessAsync(Employee("S-1-5-21-2", SalesGroup), "sales"));
    }

    [Fact]
    public async Task Admin_can_switch_restricted_module_to_everyone()
    {
        await _service.SetAccessModeAsync("sales", ModuleAccessMode.AllEmployees, "admin");

        Assert.True(await _service.CanAccessAsync(Employee("S-1-5-21-9"), "sales"));
    }

    [Fact]
    public async Task Admin_only_module_cannot_be_opened_up()
    {
        Assert.False(await _service.CanAccessAsync(Employee("S-1-5-21-9", internalNetwork: true), "admin"));
        Assert.True(await _service.CanAccessAsync(Employee("S-1-5-21-1", admin: true, internalNetwork: true), "admin"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SetAccessModeAsync("admin", ModuleAccessMode.AllEmployees, "admin"));
    }

    [Fact]
    public async Task Admin_modules_are_blocked_from_external_network()
    {
        var externalAdmin = Employee("S-1-5-21-1", admin: true);

        Assert.False(await _service.CanAccessAsync(externalAdmin, "admin"));
        Assert.True(await _service.CanAccessAsync(externalAdmin, "sales"));
    }

    [Fact]
    public async Task Internal_only_module_is_closed_outside_company_network_for_everyone()
    {
        await _service.SetInternalNetworkOnlyAsync("public", true, "admin");

        Assert.True(await _service.CanAccessAsync(Employee("S-1-5-21-1", internalNetwork: true), "public"));
        Assert.False(await _service.CanAccessAsync(Employee("S-1-5-21-1"), "public"));
        Assert.False(await _service.CanAccessAsync(Employee("S-1-5-21-2", admin: true), "public"));
        Assert.True(await _service.IsBlockedByNetworkAsync(Employee("S-1-5-21-1"), "public"));

        // The access mode stays as it was; switching back opens the module again.
        var overview = (await _service.GetOverviewAsync()).Single(o => o.Module.Key == "public");
        Assert.Equal(ModuleAccessMode.AllEmployees, overview.EffectiveMode);
        Assert.True(overview.InternalNetworkOnly);

        await _service.SetInternalNetworkOnlyAsync("public", false, "admin");
        Assert.True(await _service.CanAccessAsync(Employee("S-1-5-21-1"), "public"));
    }

    [Fact]
    public async Task Network_block_is_not_reported_when_user_lacks_permission()
    {
        await _service.SetInternalNetworkOnlyAsync("sales", true, "admin");

        Assert.False(await _service.IsBlockedByNetworkAsync(Employee("S-1-5-21-7"), "sales"));
    }

    [Fact]
    public async Task Unknown_module_is_denied() =>
        Assert.False(await _service.CanAccessAsync(Employee("S-1-5-21-1", admin: true), "nie-istnieje"));

    private static ClaimsPrincipal Employee(string sid, string? groupSid = null, bool admin = false, bool internalNetwork = false)
    {
        var principal = new ClaimsPrincipal(IntranetClaims.CreateIdentity(new DirectoryUser
        {
            AccountName = "user" + sid[^1],
            Sid = sid,
            GroupSids = groupSid is null ? [] : [groupSid],
            IsAdministrator = admin,
        }, "test", DateTimeOffset.UtcNow));

        if (internalNetwork)
        {
            principal.AddIdentity(new ClaimsIdentity([new Claim(IntranetClaims.Network, IntranetClaims.InternalNetwork)]));
        }

        return principal;
    }

    public void Dispose() => _db.Dispose();

    private sealed class Catalog : IModuleCatalog
    {
        public IReadOnlyList<ModuleDefinition> All { get; } =
        [
            new() { Key = "public", Title = "Publiczny", Route = "/p", DefaultAccess = ModuleAccessMode.AllEmployees },
            new() { Key = "sales", Title = "Sprzedaż", Route = "/s", DefaultAccess = ModuleAccessMode.Restricted },
            new() { Key = "admin", Title = "Admin", Route = "/a", DefaultAccess = ModuleAccessMode.AdministratorsOnly },
        ];

        public ModuleDefinition? Find(string key) => All.FirstOrDefault(m => m.Key == key);
    }

    private sealed class NullAudit : IAuditLog
    {
        public Task WriteAsync(string eventType, string? accountName, string? details = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AuditEntry>> GetRecentAsync(int take = 500, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditEntry>>([]);
    }
}

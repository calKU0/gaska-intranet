using System.Security.Claims;
using Gaska.Intranet.Core.Auditing;
using Gaska.Intranet.Core.Cameras;
using Gaska.Intranet.Core.Modules;
using Gaska.Intranet.Core.Organization;
using Gaska.Intranet.Core.Security;
using Gaska.Intranet.Infrastructure;
using Gaska.Intranet.Infrastructure.Access;
using Gaska.Intranet.Infrastructure.Cameras;
using Gaska.Intranet.Infrastructure.Demo;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Tests;

public sealed class CameraAccessTests : IDisposable
{
    private const string WarehouseGroup = "S-1-5-21-700";
    private const string ShopGroup = "S-1-5-21-701";
    private readonly TestDb _db = new();
    private readonly ModuleAccessService _modules;
    private readonly CameraAccess _access;

    public CameraAccessTests()
    {
        _modules = new ModuleAccessService(_db, new Catalog(), new MemoryCache(new MemoryCacheOptions()), new NullAudit(),
            Options.Create(new AccessPolicyOptions { AdminOnlyFromInternalNetwork = true }), TimeProvider.System);
        _access = new CameraAccess(_modules, new DemoCameras(TimeProvider.System));
    }

    [Fact]
    public async Task Location_rule_alone_does_not_open_the_cameras()
    {
        await GrantAsync("cameras.shop", ShopGroup);

        var user = Employee("S-1-5-21-1", ShopGroup);
        Assert.Empty(await _access.GetAllowedLocationsAsync(user));
        Assert.Null(await _access.GetViewableCameraAsync(user, "sklep-kasa"));
    }

    [Fact]
    public async Task Page_rule_alone_shows_no_location()
    {
        await GrantAsync(CameraLocations.ModuleKey, ShopGroup);

        Assert.Empty(await _access.GetAllowedLocationsAsync(Employee("S-1-5-21-1", ShopGroup)));
    }

    [Fact]
    public async Task Page_and_location_rules_show_only_that_location()
    {
        await GrantAsync(CameraLocations.ModuleKey, ShopGroup);
        await GrantAsync("cameras.shop", ShopGroup);

        var user = Employee("S-1-5-21-1", ShopGroup);
        var locations = await _access.GetAllowedLocationsAsync(user);

        Assert.Equal([CameraLocation.Shop], locations.Select(l => l.Location));
        Assert.NotNull(await _access.GetViewableCameraAsync(user, "sklep-kasa"));
        Assert.Null(await _access.GetViewableCameraAsync(user, "biuro-recepcja"));
    }

    [Fact]
    public async Task User_and_group_rules_add_up()
    {
        await GrantAsync(CameraLocations.ModuleKey, WarehouseGroup);
        await GrantAsync("cameras.warehouse-a", WarehouseGroup);
        await GrantAsync("cameras.warehouse-b", WarehouseGroup);
        await GrantAsync("cameras.outdoor", "S-1-5-21-5", PrincipalKind.User);

        var locations = await _access.GetAllowedLocationsAsync(Employee("S-1-5-21-5", WarehouseGroup));

        Assert.Equal([CameraLocation.WarehouseA, CameraLocation.WarehouseB, CameraLocation.Outdoor], locations.Select(l => l.Location));
        Assert.Equal([CameraLocation.WarehouseA, CameraLocation.WarehouseB],
            (await _access.GetAllowedLocationsAsync(Employee("S-1-5-21-6", WarehouseGroup))).Select(l => l.Location));
    }

    [Fact]
    public async Task Administrator_sees_every_location()
    {
        var admin = Employee("S-1-5-21-1", admin: true);

        Assert.Equal(CameraLocations.All.Count, (await _access.GetAllowedLocationsAsync(admin)).Count);
        Assert.NotNull(await _access.GetViewableCameraAsync(admin, "zew-wjazd"));
    }

    [Fact]
    public async Task Company_network_switch_on_a_location_hides_only_that_location_outside()
    {
        await GrantAsync(CameraLocations.ModuleKey, ShopGroup);
        await GrantAsync("cameras.shop", ShopGroup);
        await GrantAsync("cameras.outdoor", ShopGroup);
        await _modules.SetInternalNetworkOnlyAsync("cameras.shop", true, "admin");

        var inside = Employee("S-1-5-21-1", ShopGroup);
        var outside = Employee("S-1-5-21-1", ShopGroup, internalNetwork: false);

        Assert.Equal([CameraLocation.Shop, CameraLocation.Outdoor], (await _access.GetAllowedLocationsAsync(inside)).Select(l => l.Location));
        Assert.Equal([CameraLocation.Outdoor], (await _access.GetAllowedLocationsAsync(outside)).Select(l => l.Location));
        Assert.Null(await _access.GetViewableCameraAsync(outside, "sklep-kasa"));
        Assert.Null(await _access.GetViewableCameraAsync(Employee("S-1-5-21-2", admin: true, internalNetwork: false), "sklep-kasa"));
        Assert.True(await _access.IsAnyLocationBlockedByNetworkAsync(outside));
        Assert.False(await _access.IsAnyLocationBlockedByNetworkAsync(inside));
    }

    [Fact]
    public async Task Company_network_switch_on_the_page_hides_every_location_outside()
    {
        await GrantAsync(CameraLocations.ModuleKey, ShopGroup);
        await GrantAsync("cameras.shop", ShopGroup);
        await _modules.SetInternalNetworkOnlyAsync(CameraLocations.ModuleKey, true, "admin");

        Assert.Empty(await _access.GetAllowedLocationsAsync(Employee("S-1-5-21-1", ShopGroup, internalNetwork: false)));
        Assert.Empty(await _access.GetAllowedLocationsAsync(Employee("S-1-5-21-2", admin: true, internalNetwork: false)));
        Assert.Single(await _access.GetAllowedLocationsAsync(Employee("S-1-5-21-1", ShopGroup)));
    }

    [Fact]
    public async Task Without_network_switches_cameras_work_from_any_network()
    {
        await GrantAsync(CameraLocations.ModuleKey, ShopGroup);
        await GrantAsync("cameras.shop", ShopGroup);

        Assert.Single(await _access.GetAllowedLocationsAsync(Employee("S-1-5-21-1", ShopGroup, internalNetwork: false)));
    }

    [Fact]
    public async Task All_employees_mode_on_a_location_still_needs_the_page()
    {
        await _modules.SetAccessModeAsync("cameras.outdoor", ModuleAccessMode.AllEmployees, "admin");

        Assert.Empty(await _access.GetAllowedLocationsAsync(Employee("S-1-5-21-1")));

        await _modules.SetAccessModeAsync(CameraLocations.ModuleKey, ModuleAccessMode.AllEmployees, "admin");
        Assert.Equal([CameraLocation.Outdoor], (await _access.GetAllowedLocationsAsync(Employee("S-1-5-21-1"))).Select(l => l.Location));
    }

    [Fact]
    public async Task Camera_in_two_tabs_is_viewable_from_either()
    {
        var cameras = new FakeCameras(new CameraInfo("pak-1101", "Pakowanie 1101 Mag. A", [CameraLocation.Packing, CameraLocation.WarehouseA]));
        var access = new CameraAccess(_modules, cameras);
        await GrantAsync(CameraLocations.ModuleKey, WarehouseGroup);
        await GrantAsync("cameras.warehouse-a", WarehouseGroup);
        await GrantAsync(CameraLocations.ModuleKey, ShopGroup);
        await GrantAsync("cameras.shop", ShopGroup);

        Assert.NotNull(await access.GetViewableCameraAsync(Employee("S-1-5-21-1", WarehouseGroup), "pak-1101"));
        Assert.Null(await access.GetViewableCameraAsync(Employee("S-1-5-21-2", ShopGroup), "pak-1101"));
    }

    [Fact]
    public async Task Unknown_camera_is_never_viewable() =>
        Assert.Null(await _access.GetViewableCameraAsync(Employee("S-1-5-21-1", admin: true), "nie-istnieje"));

    [Fact]
    public void Location_slugs_and_keys_are_unique()
    {
        Assert.Equal(CameraLocations.All.Count, CameraLocations.All.Select(l => l.Slug).Distinct().Count());
        Assert.Equal(CameraLocations.All.Count, CameraLocations.All.Select(l => l.ModuleKey).Distinct().Count());
        Assert.Equal(CameraLocation.WarehouseB, CameraLocations.FromSlug("Magazyn-B")?.Location);
        Assert.Null(CameraLocations.FromSlug("brak"));
    }

    [Fact]
    public void Configuration_errors_are_reported()
    {
        var options = new CameraOptions
        {
            Cameras =
            [
                new CameraDefinition { Id = "a", Host = "camera-a", Location = CameraLocation.Shop },
                new CameraDefinition { Id = "A", Host = "camera-b", Location = CameraLocation.Shop },
                new CameraDefinition { Id = "zła nazwa", Host = "camera-c", Location = (CameraLocation)50 },
                new CameraDefinition { Id = "bez-adresu", Host = "" },
            ],
        };

        var errors = options.Validate().ToList();
        Assert.Contains(errors, e => e.Contains("more than once"));
        Assert.Contains(errors, e => e.Contains("may contain only"));
        Assert.Contains(errors, e => e.Contains("no Host"));
        Assert.Contains(errors, e => e.Contains("unknown Location"));
        Assert.Empty(new CameraOptions { Cameras = [new CameraDefinition { Id = "ok-1", Host = "camera", Location = CameraLocation.Office }] }.Validate());
    }

    private Task GrantAsync(string moduleKey, string sid, PrincipalKind kind = PrincipalKind.Group) =>
        _modules.GrantAsync(moduleKey, new DirectoryPrincipal(sid, "acc" + sid[^3..], "Name " + sid[^3..], kind), "admin");

    private static ClaimsPrincipal Employee(string sid, string? groupSid = null, bool admin = false, bool internalNetwork = true)
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

    /// <summary>The cameras page and its locations, as in the application's module catalog.</summary>
    private sealed class Catalog : IModuleCatalog
    {
        public IReadOnlyList<ModuleDefinition> All { get; } =
        [
            new() { Key = CameraLocations.ModuleKey, Title = "Kamery", Route = "/kamery", DefaultAccess = ModuleAccessMode.Restricted },
            .. CameraLocations.All.Select(l => new ModuleDefinition
            {
                Key = l.ModuleKey, Title = l.Title, Kind = ModuleKind.Data, DefaultAccess = ModuleAccessMode.Restricted,
            }),
        ];

        public ModuleDefinition? Find(string key) => All.FirstOrDefault(m => m.Key == key);
    }

    private sealed class NullAudit : IAuditLog
    {
        public Task WriteAsync(string eventType, string? accountName, string? details = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AuditEntry>> GetRecentAsync(int take = 500, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditEntry>>([]);
    }

    private sealed class FakeCameras(params CameraInfo[] cameras) : ICameraService
    {
        public bool IsLiveVideo => false;

        public IReadOnlyList<CameraInfo> Cameras => cameras;

        public CameraInfo? Find(string cameraId) => cameras.FirstOrDefault(c => c.Id == cameraId);

        public Task<CameraImage?> GetSnapshotAsync(string cameraId, DateTimeOffset? recordedAt = null, CancellationToken ct = default,
            CameraStreamQuality quality = CameraStreamQuality.Main) =>
            Task.FromResult<CameraImage?>(null);

        public Task<IReadOnlyList<CameraRecording>> SearchRecordingsAsync(string cameraId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CameraRecording>>([]);

        public Task<Uri> OpenLiveStreamAsync(string cameraId, CameraStreamQuality quality, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<CameraPlaybackStream> OpenPlaybackAsync(string cameraId, DateTimeOffset start, int speed = 1, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<CameraDownload?> DownloadAsync(string cameraId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult<CameraDownload?>(null);

        public Task ClosePlaybackAsync(CameraPlaybackStream stream, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> SetPlaybackSpeedAsync(CameraPlaybackStream stream, int speed, CancellationToken ct = default) =>
            Task.FromResult(false);
    }
}

using Gaska.Intranet.Core.Auditing;
using Gaska.Intranet.Core.Cameras;
using Gaska.Intranet.Infrastructure.Cameras;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Tests;

public sealed class CameraManagementTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeDevices _devices = new();
    private readonly RecordingAudit _audit = new();

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("Przyjęcia Mag. B", "Przyjecia Mag. B")]
    [InlineData("Łazienka – piętro 1", "Lazienka - pietro 1")]
    [InlineData("  Źródło   wody ", "Zrodlo wody")]
    [InlineData("Kasa \"główna\" & <zaplecze>", "Kasa glowna zaplecze")]
    public void Camera_text_has_no_polish_letters_or_odd_characters(string text, string expected) =>
        Assert.Equal(expected, ICameraAdmin.ToCameraText(text));

    [Fact]
    public void Camera_text_is_cut_to_the_camera_limit()
    {
        var text = ICameraAdmin.ToCameraText(new string('a', 40));
        Assert.Equal(ICameraAdmin.MaxCameraTextLength, text.Length);
    }

    [Fact]
    public void Users_order_comes_first_and_new_cameras_follow()
    {
        string[] defaultOrder = ["a", "b", "c", "d"];

        var ordered = CameraOrdering.Apply(defaultOrder, ["c", "removed", "a"], x => x);

        Assert.Equal(["c", "a", "b", "d"], ordered);
        Assert.Same(defaultOrder, CameraOrdering.Apply(defaultOrder, null, x => x));
    }

    [Fact]
    public void Moving_a_camera_puts_it_at_the_index()
    {
        Assert.Equal(["b", "c", "a", "d"], CameraOrdering.Move(["a", "b", "c", "d"], "a", 2));
        Assert.Equal(["d", "a", "b", "c"], CameraOrdering.Move(["a", "b", "c", "d"], "d", 0));
        Assert.Equal(["a", "b", "c", "d"], CameraOrdering.Move(["a", "b", "c", "d"], "x", 0));
    }

    [Fact]
    public async Task Configured_cameras_are_imported_once()
    {
        var options = new CameraOptions
        {
            Cameras =
            [
                new CameraDefinition { Host = "10.0.0.1", Location = CameraLocation.Office },
                new CameraDefinition { Id = "rampa", Host = "10.0.0.2", Location = CameraLocation.Packing, AlsoIn = [CameraLocation.WarehouseA] },
            ],
        };
        var store = Store(options);

        var first = await store.LoadAsync(default);
        Assert.Equal(["10-0-0-1", "rampa"], first.Select(c => c.EffectiveId).Order());
        var rampa = first.Single(c => c.EffectiveId == "rampa");
        Assert.Equal([CameraLocation.Packing, CameraLocation.WarehouseA], rampa.Locations);

        // Managed in the intranet from now on – removing a camera does not bring the configured list back.
        await store.DeleteAsync("rampa", default);
        Assert.Single(await store.LoadAsync(default));
    }

    [Fact]
    public async Task New_camera_gets_the_name_written_to_it()
    {
        var (admin, catalog) = Admin();
        _devices.Names["10.0.0.5"] = ("IP CAMERA", "Camera 01");

        var saved = await admin.SaveAsync(null, new CameraSetupForm { Host = "10.0.0.5", Name = "Przyjęcia Mag. B", Location = CameraLocation.WarehouseB }, "admin");

        Assert.Equal("10-0-0-5", saved.Id);
        // The camera takes no Polish letters – its name, shown in the intranet too, is without them.
        Assert.Equal("Przyjecia Mag. B", saved.Name);
        Assert.Equal(("Przyjecia Mag. B", "Camera 01"), _devices.Names["10.0.0.5"]);
        Assert.Equal("Przyjecia Mag. B", CameraCatalog.DisplayName(catalog.Find("10-0-0-5")!));
        Assert.Equal([AuditEvents.CameraAdded], _audit.Events);
    }

    [Fact]
    public async Task Unchanged_name_and_osd_are_not_written()
    {
        var (admin, _) = Admin();
        _devices.Names["10.0.0.5"] = ("Wejscie", "WEJSCIE");

        var saved = await admin.SaveAsync(null, new CameraSetupForm { Host = "10.0.0.5", Name = "Wejscie" }, "admin");
        var form = CameraSetupForm.From(saved);
        form.Osd = null;
        await admin.SaveAsync(saved.Id, form, "admin");

        Assert.Equal(0, _devices.Writes);
    }

    [Fact]
    public async Task Factory_name_is_shown_as_it_is_and_replaced_on_saving()
    {
        var (admin, catalog) = Admin();
        _devices.Names["10.0.0.5"] = ("IP CAMERA", "Pakowanie Nowy");
        var saved = await admin.SaveAsync(null, new CameraSetupForm { Host = "10.0.0.5", Name = "IP CAMERA" }, "admin");
        Assert.Equal("IP CAMERA", saved.Name);
        Assert.Equal(0, _devices.Writes);

        var form = CameraSetupForm.From(saved);
        form.Name = "Pakowanie Nowy";
        await admin.SaveAsync(saved.Id, form, "admin");

        Assert.Equal("Pakowanie Nowy", _devices.Names["10.0.0.5"].Device);
        Assert.Equal("Pakowanie Nowy", CameraCatalog.DisplayName(catalog.Find(saved.Id)!));
    }

    [Fact]
    public async Task Osd_change_is_written_without_polish_letters()
    {
        var (admin, _) = Admin();
        _devices.Names["10.0.0.5"] = ("Kasa", "KASA");
        var saved = await admin.SaveAsync(null, new CameraSetupForm { Host = "10.0.0.5", Name = "Kasa" }, "admin");

        var form = CameraSetupForm.From(saved);
        form.Osd = "Kasa główna";
        var changed = await admin.SaveAsync(saved.Id, form, "admin");

        Assert.Equal("Kasa glowna", changed.OsdName);
        Assert.Equal(("Kasa", "Kasa glowna"), _devices.Names["10.0.0.5"]);
    }

    [Fact]
    public async Task Name_the_camera_refuses_is_not_saved()
    {
        var (admin, catalog) = Admin();
        _devices.Names["10.0.0.5"] = ("Kasa", "KASA");
        var saved = await admin.SaveAsync(null, new CameraSetupForm { Host = "10.0.0.5", Name = "Kasa" }, "admin");
        _devices.Refuse = true;

        var form = CameraSetupForm.From(saved);
        form.Name = "Kasa 2";
        form.Location = CameraLocation.Shop;
        await Assert.ThrowsAsync<CameraAdminException>(() => admin.SaveAsync(saved.Id, form, "admin"));

        var stored = catalog.Find(saved.Id)!;
        Assert.Equal("Kasa", CameraCatalog.DisplayName(stored));
        Assert.Null(stored.Location);
    }

    [Fact]
    public async Task The_same_camera_cannot_be_added_twice()
    {
        var (admin, _) = Admin();
        _devices.Names["10.0.0.5"] = ("Kasa", "KASA");
        await admin.SaveAsync(null, new CameraSetupForm { Host = "10.0.0.5", Name = "Kasa" }, "admin");

        var ex = await Assert.ThrowsAsync<CameraAdminException>(() =>
            admin.SaveAsync(null, new CameraSetupForm { Host = "10.0.0.5", Name = "Kasa druga" }, "admin"));
        Assert.Contains("już jest na liście", ex.Message);
    }

    [Fact]
    public async Task Name_changed_on_the_camera_shows_up_in_the_intranet()
    {
        var (admin, catalog) = Admin();
        _devices.Names["10.0.0.5"] = ("Kasa", "KASA");
        var saved = await admin.SaveAsync(null, new CameraSetupForm { Host = "10.0.0.5", Name = "Kasa główna" }, "admin");

        _devices.Names["10.0.0.5"] = ("Wejscie tyl", "WEJSCIE");
        await admin.RefreshFromDevicesAsync();

        var camera = catalog.Find(saved.Id)!;
        Assert.Equal("Wejscie tyl", CameraCatalog.DisplayName(camera));
        Assert.Equal("WEJSCIE", camera.OsdName);
    }

    [Fact]
    public async Task Removed_camera_leaves_the_list()
    {
        var (admin, catalog) = Admin();
        _devices.Names["10.0.0.5"] = ("Kasa", "KASA");
        var saved = await admin.SaveAsync(null, new CameraSetupForm { Host = "10.0.0.5", Name = "Kasa" }, "admin");

        await admin.DeleteAsync(saved.Id, "admin");

        Assert.Null(catalog.Find(saved.Id));
    }

    [Fact]
    public async Task Users_order_is_saved_per_tab_and_can_be_reset()
    {
        var order = new DbCameraOrder(_db, TimeProvider.System);
        await order.SaveAsync("S-1", CameraLocation.Office, ["b", "a"]);
        await order.SaveAsync("S-1", CameraLocation.Shop, ["x"]);
        await order.SaveAsync("S-2", CameraLocation.Office, ["a", "b"]);

        var mine = await order.GetAsync("S-1");
        Assert.Equal(["b", "a"], mine[CameraLocation.Office]);
        Assert.Equal(["x"], mine[CameraLocation.Shop]);

        await order.SaveAsync("S-1", CameraLocation.Office, []);
        Assert.False((await order.GetAsync("S-1")).ContainsKey(CameraLocation.Office));
        Assert.Equal(["a", "b"], (await order.GetAsync("S-2"))[CameraLocation.Office]);
    }

    private DbCameraStore Store(CameraOptions? options = null) =>
        new(_db, Options.Create(options ?? new CameraOptions()), TimeProvider.System, NullLogger<DbCameraStore>.Instance);

    private (CameraAdminService Admin, CameraCatalog Catalog) Admin()
    {
        var store = Store();
        var catalog = new CameraCatalog(store);
        return (new CameraAdminService(catalog, store, _devices, _audit, NullLogger<CameraAdminService>.Instance), catalog);
    }

    private sealed class RecordingAudit : IAuditLog
    {
        public List<string> Events { get; } = [];

        public Task WriteAsync(string eventType, string? accountName, string? details = null, CancellationToken ct = default)
        {
            Events.Add(eventType);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEntry>> GetRecentAsync(int take = 500, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditEntry>>([]);
    }

    /// <summary>Cameras by address: (device name, OSD).</summary>
    private sealed class FakeDevices : ICameraDeviceSettings
    {
        public Dictionary<string, (string Device, string Osd)> Names { get; } = [];
        public int Writes { get; private set; }
        public bool Refuse { get; set; }

        public Task<CameraDeviceInfo> ReadAsync(CameraDefinition camera, CancellationToken ct) =>
            Names.TryGetValue(camera.Host, out var names)
                ? Task.FromResult(new CameraDeviceInfo(names.Device, names.Osd, "DS-TEST", "1"))
                : throw new CameraAdminException("Kamera nie odpowiada.");

        public Task SetDeviceNameAsync(CameraDefinition camera, string name, CancellationToken ct)
        {
            Write();
            Names[camera.Host] = (name, Names[camera.Host].Osd);
            return Task.CompletedTask;
        }

        public Task SetOsdAsync(CameraDefinition camera, string text, CancellationToken ct)
        {
            Write();
            Names[camera.Host] = (Names[camera.Host].Device, text);
            return Task.CompletedTask;
        }

        private void Write()
        {
            if (Refuse)
            {
                throw new CameraAdminException("Kamera nie przyjęła nazwy.");
            }

            Writes++;
        }
    }
}

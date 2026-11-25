using Gaska.Intranet.Core.Cameras;
using Gaska.Intranet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Infrastructure.Cameras;

/// <summary>A camera row of the intranet database (managed in Kamery → settings).</summary>
public class CameraRecord
{
    public string Id { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;
    public int HttpPort { get; set; } = 80;
    public int RtspPort { get; set; } = 554;
    public int Channel { get; set; } = 1;
    public bool HasSubStream { get; set; } = true;

    /// <summary><see cref="CameraLocation"/> name; null – "Nieprzypisane".</summary>
    public string? Location { get; set; }

    /// <summary>Further tabs, comma separated <see cref="CameraLocation"/> names.</summary>
    public string AlsoIn { get; set; } = string.Empty;

    public string? DeviceName { get; set; }
    public string? OsdName { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }

    public CameraDefinition ToDefinition() => new()
    {
        Id = Id,
        Host = Host,
        HttpPort = HttpPort,
        RtspPort = RtspPort,
        Channel = Channel,
        HasSubStream = HasSubStream,
        Location = Enum.TryParse<CameraLocation>(Location, out var location) && Enum.IsDefined(location) ? location : null,
        AlsoIn = AlsoIn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => Enum.TryParse<CameraLocation>(l, out var also) && Enum.IsDefined(also) ? also : (CameraLocation?)null)
            .OfType<CameraLocation>()
            .ToList(),
        DeviceName = DeviceName,
        OsdName = OsdName,
    };

    public void CopyFrom(CameraDefinition camera)
    {
        Id = camera.EffectiveId;
        Host = camera.Host.Trim();
        HttpPort = camera.HttpPort;
        RtspPort = camera.RtspPort;
        Channel = camera.Channel;
        HasSubStream = camera.HasSubStream;
        Location = camera.Location?.ToString();
        AlsoIn = string.Join(',', camera.AlsoIn.Where(l => l != camera.Location).Distinct());
        DeviceName = camera.DeviceName;
        OsdName = camera.OsdName;
    }
}

/// <summary>A user's own order of one camera tab.</summary>
public class CameraViewOrder
{
    public string UserSid { get; set; } = string.Empty;

    /// <summary><see cref="CameraLocation"/> name.</summary>
    public string Location { get; set; } = string.Empty;

    /// <summary>Camera ids in the user's order, comma separated.</summary>
    public string CameraIds { get; set; } = string.Empty;

    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>Where the camera list is kept – the database, or memory in demo mode.</summary>
internal interface ICameraStore
{
    Task<List<CameraDefinition>> LoadAsync(CancellationToken ct);

    Task SaveAsync(CameraDefinition camera, string? changedBy, CancellationToken ct);

    Task DeleteAsync(string id, CancellationToken ct);
}

/// <summary>
/// Cameras in the intranet database. The first time the table is empty, the list from the configuration
/// (<see cref="CameraOptions.Cameras"/>) is imported; from then on the configuration list is not read.
/// </summary>
internal sealed class DbCameraStore(
    IDbContextFactory<IntranetDbContext> dbFactory,
    IOptions<CameraOptions> options,
    TimeProvider clock,
    ILogger<DbCameraStore> logger) : ICameraStore
{
    public async Task<List<CameraDefinition>> LoadAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Cameras.AsNoTracking().ToListAsync(ct);
        var configured = options.Value.Cameras.Where(c => !string.IsNullOrWhiteSpace(c.Host)).ToList();
        if (rows.Count == 0 && configured.Count > 0)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            foreach (var camera in configured)
            {
                var row = new CameraRecord { CreatedAtUtc = now, UpdatedAtUtc = now, UpdatedBy = "appsettings" };
                row.CopyFrom(camera);
                db.Cameras.Add(row);
            }

            await db.SaveChangesAsync(ct);
            logger.LogInformation("Imported {Count} cameras from the configuration into the database", configured.Count);
            rows = await db.Cameras.AsNoTracking().ToListAsync(ct);
        }
        else if (configured.Count > 0)
        {
            logger.LogInformation("Cameras:Cameras in the configuration is ignored – the camera list is managed in the intranet");
        }

        return rows.Select(r => r.ToDefinition()).ToList();
    }

    public async Task SaveAsync(CameraDefinition camera, string? changedBy, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var row = await db.Cameras.FirstOrDefaultAsync(c => c.Id == camera.EffectiveId, ct);
        if (row is null)
        {
            row = new CameraRecord { CreatedAtUtc = now };
            db.Cameras.Add(row);
        }

        row.CopyFrom(camera);
        row.UpdatedAtUtc = now;
        row.UpdatedBy = changedBy ?? row.UpdatedBy;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Users' saved orders keep the id – removed cameras are skipped when a tab is shown.
        await db.Cameras.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
    }
}

/// <summary>
/// The current camera list in memory (every request reads it). Loaded at startup, replaced after each change in the
/// management panel.
/// </summary>
internal sealed class CameraCatalog(ICameraStore store)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile IReadOnlyList<CameraDefinition>? _cameras;

    /// <summary>Raised after the list changed (names, locations, cameras added or removed).</summary>
    public event Action? Changed;

    public IReadOnlyList<CameraDefinition> Cameras => _cameras ?? LoadNow();

    public CameraDefinition? Find(string id) =>
        Cameras.FirstOrDefault(c => string.Equals(c.EffectiveId, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The camera's own name exactly as the camera has it ("IP CAMERA" too); the address until it is read.</summary>
    public static string DisplayName(CameraDefinition camera) =>
        !string.IsNullOrWhiteSpace(camera.DeviceName) ? camera.DeviceName.Trim() : camera.Host;

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _cameras = await store.LoadAsync(ct);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    private IReadOnlyList<CameraDefinition> LoadNow()
    {
        ReloadAsync().GetAwaiter().GetResult();
        return _cameras ?? [];
    }
}

/// <summary>Loads the camera list before the application takes requests.</summary>
internal sealed class CameraCatalogLoader(CameraCatalog catalog) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => catalog.ReloadAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Each user's camera order in the intranet database.</summary>
internal sealed class DbCameraOrder(IDbContextFactory<IntranetDbContext> dbFactory, TimeProvider clock) : ICameraOrder
{
    public async Task<IReadOnlyDictionary<CameraLocation, IReadOnlyList<string>>> GetAsync(string userSid, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(userSid))
        {
            return new Dictionary<CameraLocation, IReadOnlyList<string>>();
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.CameraViewOrders.AsNoTracking().Where(o => o.UserSid == userSid).ToListAsync(ct);
        var result = new Dictionary<CameraLocation, IReadOnlyList<string>>();
        foreach (var row in rows)
        {
            if (Enum.TryParse<CameraLocation>(row.Location, out var location))
            {
                result[location] = row.CameraIds.Split(',', StringSplitOptions.RemoveEmptyEntries);
            }
        }

        return result;
    }

    public async Task SaveAsync(string userSid, CameraLocation location, IReadOnlyList<string> cameraIds, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(userSid))
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var key = location.ToString();
        var row = await db.CameraViewOrders.FirstOrDefaultAsync(o => o.UserSid == userSid && o.Location == key, ct);
        if (cameraIds.Count == 0)
        {
            if (row is not null)
            {
                db.CameraViewOrders.Remove(row);
                await db.SaveChangesAsync(ct);
            }

            return;
        }

        if (row is null)
        {
            row = new CameraViewOrder { UserSid = userSid, Location = key };
            db.CameraViewOrders.Add(row);
        }

        row.CameraIds = string.Join(',', cameraIds);
        row.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }
}

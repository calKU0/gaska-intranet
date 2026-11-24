using System.Security.Claims;

namespace Gaska.Intranet.Core.Cameras;

/// <summary>Where a camera hangs – every location is a tab with its own permission.</summary>
public enum CameraLocation
{
    Office = 1,
    WarehouseA = 2,
    WarehouseB = 3,
    Shop = 4,
    Production = 5,
    Outdoor = 6,
    Packing = 7,

    /// <summary>Cameras configured without a location – a tab of their own, so they can be found and assigned.</summary>
    Unassigned = 99,
}

/// <summary>A location tab: permission key (stored in the database – do not change), title and URL segment.</summary>
public sealed record CameraLocationInfo(CameraLocation Location, string ModuleKey, string Title, string Slug, string Description);

public static class CameraLocations
{
    /// <summary>The "Kamery" page – required in addition to the permission for a location.</summary>
    public const string ModuleKey = "cameras";

    public static IReadOnlyList<CameraLocationInfo> All { get; } =
    [
        new(CameraLocation.Office, "cameras.office", "Biuro", "biuro", "Kamery w biurze."),
        new(CameraLocation.WarehouseA, "cameras.warehouse-a", "Magazyn A", "magazyn-a", "Kamery w magazynie A."),
        new(CameraLocation.WarehouseB, "cameras.warehouse-b", "Magazyn B", "magazyn-b", "Kamery w magazynie B."),
        new(CameraLocation.Shop, "cameras.shop", "Sklep", "sklep", "Kamery w sklepie."),
        new(CameraLocation.Production, "cameras.production", "Produkcja", "produkcja", "Kamery na produkcji."),
        new(CameraLocation.Packing, "cameras.packing", "Pakowanie", "pakowanie", "Kamery na stanowiskach pakowania."),
        new(CameraLocation.Outdoor, "cameras.outdoor", "Zewnętrzne", "zewnetrzne", "Kamery zewnętrzne – wjazd, parking, plac."),
        new(CameraLocation.Unassigned, "cameras.unassigned", "Nieprzypisane", "nieprzypisane",
            "Kamery bez lokalizacji w konfiguracji – zwykle tylko dla działu IT."),
    ];

    public static CameraLocationInfo Get(CameraLocation location) => All.First(l => l.Location == location);

    public static CameraLocationInfo? FromSlug(string? slug) =>
        All.FirstOrDefault(l => string.Equals(l.Slug, slug, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A camera as shown to users – no address or credentials (those stay in the server configuration). A camera can be in
/// several tabs (e.g. a packing station in "Pakowanie" and in its warehouse); <see cref="Location"/> is the first one.
/// </summary>
public sealed record CameraInfo(string Id, string Name, IReadOnlyList<CameraLocation> Locations)
{
    public CameraInfo(string id, string name, CameraLocation location) : this(id, name, [location])
    {
    }

    public CameraLocation Location => Locations[0];
}

/// <summary>A recorded fragment found on the camera's storage.</summary>
public sealed record CameraRecording(DateTimeOffset Start, DateTimeOffset End);

public sealed record CameraImage(byte[] Content, string ContentType);

public enum CameraStreamQuality
{
    /// <summary>Sub stream – lower resolution, for the grid of tiles.</summary>
    Sub = 0,

    /// <summary>Main stream – full resolution, for a single enlarged camera.</summary>
    Main = 1,
}

/// <summary>Camera source – Hikvision via go2rtc in production, generated pictures in demo mode.</summary>
public interface ICameraService
{
    /// <summary>
    /// <c>true</c> – video goes through the stream socket (go2rtc); <c>false</c> (demo) – the player refreshes snapshots.
    /// </summary>
    bool IsLiveVideo { get; }

    IReadOnlyList<CameraInfo> Cameras { get; }

    CameraInfo? Find(string cameraId);

    /// <summary>
    /// Current frame – from the sub stream for tiles (a fraction of the size), the main stream for a single camera.
    /// <paramref name="recordedAt"/> is used by the demo source only (a frame "from a recording").
    /// </summary>
    Task<CameraImage?> GetSnapshotAsync(string cameraId, DateTimeOffset? recordedAt = null, CancellationToken ct = default,
        CameraStreamQuality quality = CameraStreamQuality.Main);

    Task<IReadOnlyList<CameraRecording>> SearchRecordingsAsync(string cameraId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    /// <summary>Makes sure the live stream exists in the media server; returns the socket address (server side only).</summary>
    Task<Uri> OpenLiveStreamAsync(string cameraId, CameraStreamQuality quality, CancellationToken ct = default);

    /// <summary>
    /// Creates a temporary stream playing the recording from <paramref name="start"/>. <paramref name="speed"/> above 1
    /// makes the camera send the recording faster (the player plays it at the same rate).
    /// </summary>
    Task<CameraPlaybackStream> OpenPlaybackAsync(string cameraId, DateTimeOffset start, int speed = 1, CancellationToken ct = default);

    /// <summary>Removes the temporary playback stream (viewer disconnected).</summary>
    Task ClosePlaybackAsync(CameraPlaybackStream stream, CancellationToken ct = default);

    /// <summary>Changes the speed of a playing recording in place – no new connection, it goes on from where it is.</summary>
    Task<bool> SetPlaybackSpeedAsync(CameraPlaybackStream stream, int speed, CancellationToken ct = default);

    /// <summary>Recording files between <paramref name="from"/> and <paramref name="to"/> as one file to save; null when there are none.</summary>
    Task<CameraDownload?> DownloadAsync(string cameraId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}

public sealed record CameraPlaybackStream(string Name, Uri SocketUri);

/// <summary>A recording to save: name and type of the file, and the writer that produces its content.</summary>
public sealed record CameraDownload(string FileName, string ContentType, Func<Stream, CancellationToken, Task> WriteToAsync);

/// <summary>Playback speeds offered in the player (the camera sends the recording this many times faster).</summary>
public static class CameraPlaybackSpeeds
{
    public static IReadOnlyList<int> All { get; } = [1, 2, 4, 8];

    public static int Normalize(int speed) => All.Contains(speed) ? speed : 1;

    /// <summary>
    /// The recording time shown after <paramref name="mediaSeconds"/> of video from <paramref name="start"/>. Cameras skip
    /// the gaps between recordings, so the time runs through the recordings, not the clock.
    /// </summary>
    public static DateTimeOffset PositionAt(DateTimeOffset start, double mediaSeconds, IReadOnlyList<CameraRecording> recordings)
    {
        var remaining = TimeSpan.FromSeconds(Math.Max(0, mediaSeconds));
        var position = start;
        foreach (var recording in recordings.OrderBy(r => r.Start))
        {
            if (recording.End <= position)
            {
                continue;
            }

            var from = recording.Start > position ? recording.Start : position;
            var length = recording.End - from;
            if (remaining <= length)
            {
                return from + remaining;
            }

            remaining -= length;
            position = recording.End;
        }

        return position + remaining;
    }

    /// <summary>
    /// Where a playback chosen at <paramref name="time"/> really starts: in the recording that contains it, else at the
    /// next recording (a camera refuses a playback with nothing recorded); null when nothing is recorded after it.
    /// </summary>
    public static DateTimeOffset? StartAt(DateTimeOffset time, IReadOnlyList<CameraRecording> recordings)
    {
        foreach (var recording in recordings.OrderBy(r => r.Start))
        {
            if (recording.End > time)
            {
                return recording.Start > time ? recording.Start : time;
            }
        }

        return null;
    }
}

/// <summary>
/// Who may watch which camera: the "Kamery" page permission AND the location's permission – both ordinary module rules,
/// including the "company network only" switch of the permissions panel.
/// </summary>
public interface ICameraAccess
{
    Task<IReadOnlyList<CameraLocationInfo>> GetAllowedLocationsAsync(ClaimsPrincipal user, CancellationToken ct = default);

    /// <summary>The camera if the user may watch it; <c>null</c> for an unknown camera or no permission.</summary>
    Task<CameraInfo?> GetViewableCameraAsync(ClaimsPrincipal user, string cameraId, CancellationToken ct = default);

    /// <summary>
    /// Some location would be visible from the company network but not from the user's current one ("company network
    /// only" switch) – shown as a hint instead of "no locations".
    /// </summary>
    Task<bool> IsAnyLocationBlockedByNetworkAsync(ClaimsPrincipal user, CancellationToken ct = default);
}

/// <summary>The camera or the media server did not answer.</summary>
public sealed class CameraUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

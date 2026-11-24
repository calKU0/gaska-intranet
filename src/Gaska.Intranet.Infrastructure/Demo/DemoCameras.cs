using System.Globalization;
using System.Net;
using System.Text;
using Gaska.Intranet.Core.Cameras;

namespace Gaska.Intranet.Infrastructure.Demo;

/// <summary>
/// Demo cameras (Development + Demo:Enabled): a few cameras per location with generated pictures (name + clock) and
/// sample recordings – screenshots for the user manual and UI work without real cameras or go2rtc.
/// </summary>
internal sealed class DemoCameras(TimeProvider clock) : ICameraService
{
    private static readonly IReadOnlyList<CameraInfo> All =
    [
        new("biuro-recepcja", "Recepcja", CameraLocation.Office),
        new("biuro-korytarz", "Korytarz – piętro 1", CameraLocation.Office),
        new("biuro-serwerownia", "Serwerownia", CameraLocation.Office),
        new("ma-rampa", "Rampa Mag. A", CameraLocation.WarehouseA),
        new("ma-antresola-parter", "Antresola Mag. A Parter", CameraLocation.WarehouseA),
        new("mb-rampa", "Rampa Mag. B", CameraLocation.WarehouseB),
        new("mb-przyjecia", "Przyjęcia Mag. B", CameraLocation.WarehouseB),
        new("mb-zwroty", "Zwroty Mag. B", CameraLocation.WarehouseB),
        new("mb-kosze", "Kosze Mag. B", CameraLocation.WarehouseB),
        new("nieprzypisana-1", "Stanowisko 1101", CameraLocation.Unassigned),
        new("sklep-kasa", "Kasa", CameraLocation.Shop),
        new("sklep-sala", "Sala sprzedaży", CameraLocation.Shop),
        new("prod-hala", "Hala produkcyjna", CameraLocation.Production),
        new("prod-lakiernia", "Lakiernia", CameraLocation.Production),
        new("pak-stanowisko-1", "Pakowanie – stanowisko 1", CameraLocation.Packing),
        new("pak-stanowisko-2", "Pakowanie – stanowisko 2", CameraLocation.Packing),
        new("zew-wjazd", "Brama wjazdowa", CameraLocation.Outdoor),
        new("zew-parking", "Parking", CameraLocation.Outdoor),
        new("zew-plac", "Plac manewrowy", CameraLocation.Outdoor),
    ];

    public bool IsLiveVideo => false;

    public IReadOnlyList<CameraInfo> Cameras => All;

    public CameraInfo? Find(string cameraId) => All.FirstOrDefault(c => string.Equals(c.Id, cameraId, StringComparison.OrdinalIgnoreCase));

    public Task<CameraImage?> GetSnapshotAsync(string cameraId, DateTimeOffset? recordedAt = null, CancellationToken ct = default,
        CameraStreamQuality quality = CameraStreamQuality.Main)
    {
        var camera = Find(cameraId);
        if (camera is null)
        {
            return Task.FromResult<CameraImage?>(null);
        }

        var time = TimeZoneInfo.ConvertTime(recordedAt ?? clock.GetUtcNow(), clock.LocalTimeZone);
        return Task.FromResult<CameraImage?>(new CameraImage(Encoding.UTF8.GetBytes(Picture(camera, time, recordedAt.HasValue)), "image/svg+xml"));
    }

    public Task<IReadOnlyList<CameraRecording>> SearchRecordingsAsync(string cameraId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var seed = StableHash(cameraId);
        var recordings = new List<CameraRecording>();

        // Motion-style recording: a handful of blocks per day, different for every camera.
        for (var day = TimeZoneInfo.ConvertTime(from, clock.LocalTimeZone).Date; day <= TimeZoneInfo.ConvertTime(to, clock.LocalTimeZone).Date; day = day.AddDays(1))
        {
            var random = new Random(seed ^ day.DayOfYear);
            var minute = 5 * 60 + random.Next(0, 90);
            while (minute < 22 * 60)
            {
                var length = random.Next(20, 180);
                var start = Local(day.AddMinutes(minute));
                var end = Local(day.AddMinutes(minute + length));
                if (start < now && end > from && start < to)
                {
                    recordings.Add(new CameraRecording(start < from ? from : start, end > now ? now : end > to ? to : end));
                }

                minute += length + random.Next(10, 70);
            }
        }

        return Task.FromResult<IReadOnlyList<CameraRecording>>(recordings);
    }

    public Task<Uri> OpenLiveStreamAsync(string cameraId, CameraStreamQuality quality, CancellationToken ct = default) =>
        throw new CameraUnavailableException("Demo cameras have no video stream – the player refreshes pictures.");

    public Task<CameraPlaybackStream> OpenPlaybackAsync(string cameraId, DateTimeOffset start, int speed = 1, CancellationToken ct = default) =>
        throw new CameraUnavailableException("Demo cameras have no video stream – the player refreshes pictures.");

    public Task ClosePlaybackAsync(CameraPlaybackStream stream, CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> SetPlaybackSpeedAsync(CameraPlaybackStream stream, int speed, CancellationToken ct = default) =>
        Task.FromResult(true);

    /// <summary>Demo cameras have no recording files.</summary>
    public Task<CameraDownload?> DownloadAsync(string cameraId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        Task.FromResult<CameraDownload?>(null);

    private DateTimeOffset Local(DateTime wall) => new(wall, clock.LocalTimeZone.GetUtcOffset(wall));

    private static int StableHash(string value)
    {
        var hash = 17;
        foreach (var c in value)
        {
            hash = unchecked(hash * 31 + c);
        }

        return hash & 0x7fffffff;
    }

    /// <summary>A stylised "camera view": scene shapes per location, camera name and an on-screen clock like Hikvision's OSD.</summary>
    private static string Picture(CameraInfo camera, DateTimeOffset time, bool recording)
    {
        var hue = StableHash(camera.Id) % 360;
        var scene = camera.Location switch
        {
            CameraLocation.Office => """
                <rect x="60" y="250" width="260" height="20" fill="#5b5f66"/><rect x="80" y="270" width="12" height="90" fill="#44474d"/>
                <rect x="290" y="270" width="12" height="90" fill="#44474d"/><rect x="140" y="170" width="110" height="75" rx="4" fill="#23262b"/>
                <rect x="400" y="120" width="180" height="240" fill="#3b3f46"/><rect x="415" y="135" width="150" height="100" fill="#8fb4d9" opacity=".55"/>
                """,
            CameraLocation.WarehouseA or CameraLocation.WarehouseB => """
                <g fill="#c77d2a"><rect x="40" y="90" width="14" height="270"/><rect x="250" y="90" width="14" height="270"/><rect x="380" y="90" width="14" height="270"/><rect x="590" y="90" width="14" height="270"/></g>
                <g fill="#7c5a3a"><rect x="54" y="140" width="196" height="8"/><rect x="54" y="230" width="196" height="8"/><rect x="394" y="140" width="196" height="8"/><rect x="394" y="230" width="196" height="8"/></g>
                <g fill="#b8935f"><rect x="70" y="100" width="60" height="40"/><rect x="150" y="110" width="70" height="30"/><rect x="80" y="190" width="80" height="40"/><rect x="410" y="96" width="80" height="44"/><rect x="500" y="190" width="70" height="40"/><rect x="420" y="300" width="90" height="60"/></g>
                """,
            CameraLocation.Shop => """
                <rect x="60" y="230" width="200" height="130" fill="#3b3f46"/><rect x="80" y="200" width="70" height="30" fill="#23262b"/>
                <g fill="#5d7f9e"><rect x="340" y="110" width="240" height="18"/><rect x="340" y="180" width="240" height="18"/><rect x="340" y="250" width="240" height="18"/></g>
                <g fill="#d9a441"><rect x="350" y="80" width="30" height="30"/><rect x="400" y="86" width="40" height="24"/><rect x="460" y="150" width="34" height="30"/><rect x="360" y="220" width="50" height="30"/></g>
                """,
            CameraLocation.Production or CameraLocation.Packing => """
                <rect x="0" y="300" width="640" height="40" fill="#3b3f46"/><g fill="#6b7078"><circle cx="80" cy="320" r="16"/><circle cx="560" cy="320" r="16"/></g>
                <g fill="#9aa3ad"><rect x="120" y="270" width="70" height="30"/><rect x="300" y="262" width="80" height="38"/><rect x="470" y="276" width="60" height="24"/></g>
                <rect x="250" y="80" width="140" height="150" fill="#4d535c"/><rect x="270" y="100" width="100" height="60" fill="#79c27b" opacity=".6"/>
                """,
            _ => """
                <rect x="0" y="230" width="640" height="130" fill="#4a4d52"/><g stroke="#e6e6e6" stroke-width="6" stroke-dasharray="30 26"><line x1="0" y1="300" x2="640" y2="300"/></g>
                <rect x="380" y="80" width="230" height="150" fill="#6b6f76"/><rect x="400" y="110" width="60" height="120" fill="#3a3d42"/>
                <rect x="90" y="250" width="150" height="50" rx="10" fill="#2f6db5"/><g fill="#1b1d20"><circle cx="120" cy="300" r="14"/><circle cx="210" cy="300" r="14"/></g>
                """,
        };

        var name = WebUtility.HtmlEncode(camera.Name);
        var location = WebUtility.HtmlEncode(CameraLocations.Get(camera.Location).Title);
        var clockText = time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var badge = recording ? "NAGRANIE" : "DEMO";
        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 640 360" width="640" height="360">
              <defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="hsl({hue},18%,30%)"/><stop offset="1" stop-color="hsl({hue},14%,16%)"/></linearGradient></defs>
              <rect width="640" height="360" fill="url(#g)"/>
              {scene}
              <rect width="640" height="360" fill="none" stroke="#000" stroke-opacity=".25" stroke-width="40"/>
              <g font-family="Consolas, monospace" font-size="20" fill="#fff" stroke="#000" stroke-width="3" paint-order="stroke">
                <text x="16" y="32">{clockText}</text>
                <text x="16" y="344">{location} · {name}</text>
              </g>
              <g font-family="Segoe UI, sans-serif" font-size="14" font-weight="700">
                <rect x="{(recording ? 522 : 564)}" y="14" width="{(recording ? 104 : 62)}" height="24" rx="12" fill="{(recording ? "#c62828" : "#1565c0")}"/>
                <text x="{(recording ? 574 : 595)}" y="31" fill="#fff" text-anchor="middle">{badge}</text>
              </g>
            </svg>
            """;
    }
}

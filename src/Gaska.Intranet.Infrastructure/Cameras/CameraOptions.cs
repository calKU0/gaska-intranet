using Gaska.Intranet.Core.Cameras;

namespace Gaska.Intranet.Infrastructure.Cameras;

/// <summary>
/// Hikvision cameras (section "Cameras"). Addresses and the service account belong in appsettings.Local.json only –
/// the repository is public.
/// </summary>
public sealed class CameraOptions
{
    public const string SectionName = "Cameras";

    /// <summary>go2rtc API on the application server – must listen on localhost only (see docs/kamery-go2rtc.md).</summary>
    public string Go2RtcUrl { get; set; } = "http://127.0.0.1:1984";

    /// <summary>Service account used for every camera (ideally a "viewer" account with playback rights).</summary>
    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Zone of the times the cameras write with a "Z" suffix (recording search, RTSP playback). Current Hikvision firmware
    /// uses real UTC – the default. Some older models write their local time with a "Z": then set e.g. "Europe/Warsaw".
    /// </summary>
    public string DeviceTimeZone { get; set; } = "UTC";

    /// <summary>
    /// ffmpeg.exe for downloads as ordinary MP4 (e.g. "C:\go2rtc\ffmpeg.exe"). Empty – downloads are the cameras' own
    /// files (MPEG program stream with a Hikvision header, played by VLC).
    /// </summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>Camera HTTP (ISAPI) request limit.</summary>
    public int TimeoutSeconds { get; set; } = 8;

    public List<CameraDefinition> Cameras { get; set; } = [];

    /// <summary>
    /// Polish words for camera names. Cameras keep only some Polish letters (ą, ę, ł, ż become a, e, l, z), so names read
    /// from a camera get whole words restored, e.g. "Pietro" → "Piętro". The key is compared without Polish letters and
    /// case. Entries from the configuration are added to these.
    /// </summary>
    public Dictionary<string, string> PolishWords { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Pietro"] = "Piętro", ["Pietra"] = "Piętra", ["Tyl"] = "Tył", ["Przod"] = "Przód", ["Dol"] = "Dół",
        ["Gora"] = "Góra", ["Przyjecia"] = "Przyjęcia", ["Przyjecie"] = "Przyjęcie", ["Wejscie"] = "Wejście",
        ["Wyjscie"] = "Wyjście", ["Zewnetrzne"] = "Zewnętrzne", ["Zewnatrz"] = "Zewnątrz", ["Ksiegowosc"] = "Księgowość",
        ["Zdjecia"] = "Zdjęcia", ["Stodola"] = "Stodoła", ["Ogrod"] = "Ogród", ["Scianka"] = "Ścianka",
        ["Sciana"] = "Ściana", ["Wysylka"] = "Wysyłka", ["Polnoc"] = "Północ", ["Poludnie"] = "Południe",
        ["Wschod"] = "Wschód", ["Zachod"] = "Zachód", ["Lazienka"] = "Łazienka",
    };

    /// <summary>Configuration errors (shown at startup).</summary>
    public IEnumerable<string> Validate()
    {
        foreach (var camera in Cameras.Where(c => string.IsNullOrWhiteSpace(c.Host)))
        {
            yield return $"Camera \"{camera.Id}\" has no Host.";
        }

        var cameras = Cameras.Where(c => !string.IsNullOrWhiteSpace(c.Host)).ToList();
        foreach (var dup in cameras.GroupBy(c => c.EffectiveId, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            yield return $"Camera id \"{dup.Key}\" is used more than once.";
        }

        foreach (var camera in cameras)
        {
            if (!camera.EffectiveId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                yield return $"Camera id \"{camera.EffectiveId}\" may contain only letters, digits, '-' and '_'.";
            }

            if (camera.Location is { } location && !Enum.IsDefined(location))
            {
                yield return $"Camera \"{camera.EffectiveId}\" has an unknown Location.";
            }

            if (camera.AlsoIn.Any(l => !Enum.IsDefined(l)))
            {
                yield return $"Camera \"{camera.EffectiveId}\" has an unknown location in AlsoIn.";
            }
        }
    }

    public TimeZoneInfo ResolveTimeZone()
    {
        if (string.IsNullOrWhiteSpace(DeviceTimeZone) || DeviceTimeZone.Equals("UTC", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(DeviceTimeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
    }
}

public sealed class CameraDefinition
{
    /// <summary>
    /// Permanent id used in URLs and the audit log, e.g. "magazyn-a-rampa". Optional – empty: made from the address
    /// ("10-0-0-43").
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Name shown in the intranet. Optional – empty: the camera's own name (OSD "Camera Name" in its web panel).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Location tab; empty (null) – the "Nieprzypisane" tab.</summary>
    public CameraLocation? Location { get; set; }

    /// <summary>Further tabs showing the same camera, e.g. a packing station also in its warehouse: ["WarehouseA"].</summary>
    public List<CameraLocation> AlsoIn { get; set; } = [];

    /// <summary>All tabs of the camera – the "Nieprzypisane" tab when it has none.</summary>
    public IReadOnlyList<CameraLocation> Locations
    {
        get
        {
            var all = (Location is { } main ? [main] : new List<CameraLocation>()).Concat(AlsoIn).Distinct().ToList();
            return all.Count > 0 ? all : [CameraLocation.Unassigned];
        }
    }

    public string EffectiveId =>
        string.IsNullOrWhiteSpace(Id) ? Host.Trim().Replace('.', '-').Replace(':', '-') : Id.Trim();

    /// <summary>Camera (or recorder) address in the camera network.</summary>
    public string Host { get; set; } = string.Empty;

    public int HttpPort { get; set; } = 80;

    public int RtspPort { get; set; } = 554;

    /// <summary>Channel number – 1 for a single camera; the channel on a recorder.</summary>
    public int Channel { get; set; } = 1;

    /// <summary>Sub stream for the grid (Hikvision "02"); set to false when the camera's sub stream is disabled.</summary>
    public bool HasSubStream { get; set; } = true;
}

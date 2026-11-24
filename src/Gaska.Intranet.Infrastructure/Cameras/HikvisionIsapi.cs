using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Gaska.Intranet.Core.Cameras;

namespace Gaska.Intranet.Infrastructure.Cameras;

/// <summary>Hikvision ISAPI / RTSP formats – pure functions, unit tested.</summary>
internal static class HikvisionIsapi
{
    /// <summary>Main stream track / channel id: channel 1 → 101, sub stream → 102.</summary>
    public static int TrackId(int channel, CameraStreamQuality quality = CameraStreamQuality.Main) =>
        channel * 100 + (quality == CameraStreamQuality.Main ? 1 : 2);

    public static string SnapshotPath(int channel, CameraStreamQuality quality = CameraStreamQuality.Main) =>
        $"/ISAPI/Streaming/channels/{TrackId(channel, quality)}/picture";

    public const string SearchPath = "/ISAPI/ContentMgmt/search";

    public const string DeviceInfoPath = "/ISAPI/System/deviceInfo";

    /// <summary>Video input channel – its <c>name</c> is the "Camera Name" of the OSD settings in the web panel.</summary>
    public static string ChannelPath(int channel) => $"/ISAPI/System/Video/inputs/channels/{channel}";

    /// <summary>First element with this local name (any ISAPI namespace), trimmed; null when missing or empty.</summary>
    public static string? ReadElement(string xml, string name)
    {
        var value = XDocument.Parse(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Factory names nobody chose ("Camera 01", "IP CAMERA", "IPdome") – not worth showing.</summary>
    public static bool IsDefaultName(string? name) =>
        string.IsNullOrWhiteSpace(name) ||
        System.Text.RegularExpressions.Regex.IsMatch(name.Trim(), @"^(camera\s*\d+\W*|ip\s*camera(\s*\d+)?|ipdome|embedded net dvr)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Text as written by a camera turned back into Polish: cameras store ć, ś, ź (and capital Ń, Ó) as an apostrophe
    /// plus the letter ("Wej'scie"), and drop the marks of ą, ę, ł, ż altogether – <paramref name="words"/> restores whole
    /// words ("Pietro" → "Piętro"), compared without Polish letters.
    /// </summary>
    public static string ToPolish(string text, IReadOnlyDictionary<string, string>? words = null)
    {
        var result = System.Text.RegularExpressions.Regex.Replace(text, "'([csznoCSZNO])", m => m.Groups[1].Value switch
        {
            "c" => "ć", "s" => "ś", "z" => "ź", "n" => "ń", "o" => "ó",
            "C" => "Ć", "S" => "Ś", "Z" => "Ź", "N" => "Ń", _ => "Ó",
        });

        if (words is null || words.Count == 0)
        {
            return result;
        }

        var byPlain = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in words)
        {
            byPlain[WithoutPolishLetters(key)] = value;
        }

        return System.Text.RegularExpressions.Regex.Replace(result, @"\p{L}+", m =>
        {
            if (!byPlain.TryGetValue(WithoutPolishLetters(m.Value), out var word))
            {
                return m.Value;
            }

            // Keep the camera's capitalisation: "TYL" → "TYŁ", "tyl" → "tył", "Tyl" → "Tył".
            return m.Value.All(char.IsUpper) && m.Value.Length > 1 ? word.ToUpperInvariant()
                : char.IsLower(m.Value[0]) ? word.ToLowerInvariant()
                : char.ToUpperInvariant(word[0]) + word[1..];
        });
    }

    private static string WithoutPolishLetters(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(c switch
            {
                'ą' => 'a', 'ć' => 'c', 'ę' => 'e', 'ł' => 'l', 'ń' => 'n', 'ó' => 'o', 'ś' => 's', 'ź' => 'z', 'ż' => 'z',
                'Ą' => 'A', 'Ć' => 'C', 'Ę' => 'E', 'Ł' => 'L', 'Ń' => 'N', 'Ó' => 'O', 'Ś' => 'S', 'Ź' => 'Z', 'Ż' => 'Z',
                _ => c,
            });
        }

        return builder.ToString();
    }

    /// <summary>The first name that is not a factory default (device name, then OSD name); null when none.</summary>
    public static string? PickName(string? preferred, string? fallback) =>
        !IsDefaultName(preferred) ? Tidy(preferred!) : !IsDefaultName(fallback) ? Tidy(fallback!) : null;

    // Some models do not take spaces in names – "Stanowisko_1116" reads better as "Stanowisko 1116".
    private static string Tidy(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name.Replace('_', ' '), @"\s+", " ").Trim();

    /// <summary>
    /// Device times are written with a "Z" suffix – real UTC on current firmware, local wall-clock time on some older
    /// models; <paramref name="deviceZone"/> says which (<see cref="CameraOptions.DeviceTimeZone"/>).
    /// </summary>
    public static string FormatIsoTime(DateTimeOffset time, TimeZoneInfo deviceZone) =>
        TimeZoneInfo.ConvertTime(time, deviceZone).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>RTSP playback time: <c>20261124T080000Z</c>, also the camera's wall clock.</summary>
    public static string FormatRtspTime(DateTimeOffset time, TimeZoneInfo deviceZone) =>
        TimeZoneInfo.ConvertTime(time, deviceZone).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseDeviceTime(string value, TimeZoneInfo deviceZone)
    {
        // Ignore the suffix and fractions – the first 19 characters are the time in the device zone.
        var text = value.Trim();
        var wall = DateTime.ParseExact(text.Length > 19 ? text[..19] : text, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        return new DateTimeOffset(wall, deviceZone.GetUtcOffset(wall));
    }

    public static string BuildSearchRequest(int channel, DateTimeOffset from, DateTimeOffset to, int position, TimeZoneInfo deviceZone,
        Guid searchId, int maxResults = 100) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <CMSearchDescription>
          <searchID>{searchId.ToString().ToUpperInvariant()}</searchID>
          <trackList><trackID>{TrackId(channel)}</trackID></trackList>
          <timeSpanList>
            <timeSpan>
              <startTime>{FormatIsoTime(from, deviceZone)}</startTime>
              <endTime>{FormatIsoTime(to, deviceZone)}</endTime>
            </timeSpan>
          </timeSpanList>
          <maxResults>{maxResults}</maxResults>
          <searchResultPostion>{position}</searchResultPostion>
          <metadataList><metadataDescriptor>//recordType.meta.std-cgi.com</metadataDescriptor></metadataList>
        </CMSearchDescription>
        """;

    /// <summary>One recording file on the camera's storage: its time span and the URI the camera downloads it by.</summary>
    public sealed record RecordingFile(CameraRecording Recording, string? PlaybackUri);

    public sealed record SearchPage(IReadOnlyList<RecordingFile> Files, bool HasMore)
    {
        public IReadOnlyList<CameraRecording> Recordings => Files.Select(f => f.Recording).ToList();
    }

    /// <summary>Reads a <c>CMSearchResult</c> (any ISAPI schema namespace version).</summary>
    public static SearchPage ParseSearchResult(string xml, TimeZoneInfo deviceZone)
    {
        var doc = XDocument.Parse(xml);
        var status = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "responseStatusStrg")?.Value.Trim() ?? string.Empty;

        var files = new List<RecordingFile>();
        foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "searchMatchItem"))
        {
            var span = item.Elements().FirstOrDefault(e => e.Name.LocalName == "timeSpan");
            var start = span?.Elements().FirstOrDefault(e => e.Name.LocalName == "startTime")?.Value;
            var end = span?.Elements().FirstOrDefault(e => e.Name.LocalName == "endTime")?.Value;
            if (string.IsNullOrWhiteSpace(start) || string.IsNullOrWhiteSpace(end))
            {
                continue;
            }

            var recording = new CameraRecording(ParseDeviceTime(start, deviceZone), ParseDeviceTime(end, deviceZone));
            var uri = item.Descendants().FirstOrDefault(e => e.Name.LocalName == "playbackURI")?.Value.Trim();
            if (recording.End > recording.Start)
            {
                files.Add(new RecordingFile(recording, string.IsNullOrEmpty(uri) ? null : uri));
            }
        }

        return new SearchPage(files, string.Equals(status, "MORE", StringComparison.OrdinalIgnoreCase));
    }

    public const string DownloadPath = "/ISAPI/ContentMgmt/download";

    /// <summary>Asks for one recording file (cameras download whole files – the times in the URI do not cut them).</summary>
    public static string BuildDownloadRequest(string playbackUri) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <downloadRequest version="1.0" xmlns="http://www.hikvision.com/ver20/XMLSchema">
          <playbackURI>{System.Security.SecurityElement.Escape(playbackUri)}</playbackURI>
        </downloadRequest>
        """;

    /// <summary>
    /// Length of the Hikvision file header ("IMKH", 40 bytes) in front of the MPEG program stream – kept on the first file
    /// of a download, dropped from the next ones so the files join into one stream.
    /// </summary>
    public const int FileHeaderLength = 40;

    public static bool HasFileHeader(ReadOnlySpan<byte> data) => data.Length >= FileHeaderLength && data[..4].SequenceEqual("IMKH"u8);

    /// <summary>Joins touching / overlapping fragments (cameras cut recordings into many short files).</summary>
    public static IReadOnlyList<CameraRecording> Merge(IEnumerable<CameraRecording> recordings, TimeSpan gap)
    {
        var merged = new List<CameraRecording>();
        foreach (var r in recordings.OrderBy(r => r.Start))
        {
            if (merged.Count > 0 && r.Start - merged[^1].End <= gap)
            {
                var last = merged[^1];
                merged[^1] = last with { End = r.End > last.End ? r.End : last.End };
            }
            else
            {
                merged.Add(r);
            }
        }

        return merged;
    }

    public static string LiveRtspUrl(CameraDefinition camera, CameraStreamQuality quality, string user, string password) =>
        $"rtsp://{UserInfo(user, password)}{camera.Host}:{camera.RtspPort}/Streaming/Channels/" +
        TrackId(camera.Channel, quality == CameraStreamQuality.Sub && camera.HasSubStream ? CameraStreamQuality.Sub : CameraStreamQuality.Main);

    public static string PlaybackRtspUrl(CameraDefinition camera, DateTimeOffset start, DateTimeOffset end, string user, string password,
        TimeZoneInfo deviceZone) =>
        // The same form as the playbackURI the camera returns from a search (slash before the query).
        $"rtsp://{UserInfo(user, password)}{camera.Host}:{camera.RtspPort}/Streaming/tracks/{TrackId(camera.Channel)}/" +
        $"?starttime={FormatRtspTime(start, deviceZone)}&endtime={FormatRtspTime(end, deviceZone)}";

    private static string UserInfo(string user, string password) =>
        string.IsNullOrEmpty(user) ? string.Empty : $"{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(password)}@";
}

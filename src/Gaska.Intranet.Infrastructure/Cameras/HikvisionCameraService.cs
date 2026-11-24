using System.Text;
using Gaska.Intranet.Core.Cameras;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Infrastructure.Cameras;

/// <summary>
/// Hikvision cameras: snapshots and recording search over ISAPI (HTTP Digest), video through go2rtc. Recordings are
/// kept by each camera on its network storage (NAS), so the search and the RTSP playback both go to the camera.
/// </summary>
internal sealed class HikvisionCameraService(
    IHttpClientFactory httpFactory,
    Go2RtcClient go2rtc,
    IMemoryCache cache,
    IOptions<CameraOptions> options,
    TimeProvider clock,
    ILogger<HikvisionCameraService> logger) : ICameraService
{
    public const string HttpClientName = "hikvision";

    /// <summary>Same credentials as <see cref="HttpClientName"/>, without the short timeout – recording files are large.</summary>
    public const string DownloadClientName = "hikvision-download";

    /// <summary>How long one playback stream may run before the viewer has to pick the time again.</summary>
    private static readonly TimeSpan PlaybackLength = TimeSpan.FromHours(2);

    private static readonly TimeSpan SnapshotCacheTime = TimeSpan.FromSeconds(2);

    // go2rtc source options: no two-way audio (cameras reject the ONVIF backchannel on playback), and a longer read
    // timeout for playback – a camera recording to network storage needs several seconds to find the recording.
    private const string LiveSourceOptions = "#backchannel=0";
    private const string PlaybackSourceOptions = "#backchannel=0#timeout=30";

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RtspRangeRelay> _relays = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _deviceNames = new(StringComparer.OrdinalIgnoreCase);

    private CameraOptions Options => options.Value;

    public bool IsLiveVideo => true;

    /// <summary>Configured cameras; those without a location are in the "Nieprzypisane" tab.</summary>
    public IReadOnlyList<CameraInfo> Cameras =>
        Options.Cameras
            .Where(c => !string.IsNullOrWhiteSpace(c.Host))
            .Select(c => new CameraInfo(c.EffectiveId, DisplayName(c), c.Locations))
            .ToList();

    public CameraInfo? Find(string cameraId) => Cameras.FirstOrDefault(c => string.Equals(c.Id, cameraId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The configured name, else the camera's own name (read by <see cref="RefreshNamesAsync"/>), else the address.</summary>
    private string DisplayName(CameraDefinition camera) =>
        !string.IsNullOrWhiteSpace(camera.Name) ? camera.Name.Trim()
        : _deviceNames.TryGetValue(camera.EffectiveId, out var name) ? name
        : camera.Host;

    /// <summary>
    /// Reads the names set in the cameras' web panels ("Device Name", else the OSD "Camera Name") for cameras without a
    /// configured name. Cameras that do not answer keep their previous name.
    /// </summary>
    public async Task RefreshNamesAsync(CancellationToken ct)
    {
        var cameras = Options.Cameras.Where(c => string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.Host)).ToList();
        await Parallel.ForEachAsync(cameras, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (camera, token) =>
        {
            try
            {
                var device = await GetTextAsync(camera, HikvisionIsapi.DeviceInfoPath, token);
                var deviceName = device is null ? null : HikvisionIsapi.ReadElement(device, "deviceName");
                string? channelName = null;
                if (HikvisionIsapi.IsDefaultName(deviceName))
                {
                    var channel = await GetTextAsync(camera, HikvisionIsapi.ChannelPath(camera.Channel), token);
                    channelName = channel is null ? null : HikvisionIsapi.ReadElement(channel, "name");
                }

                if (HikvisionIsapi.PickName(deviceName, channelName) is { } name)
                {
                    _deviceNames[camera.EffectiveId] = HikvisionIsapi.ToPolish(name, Options.PolishWords);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException && !token.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Camera {Camera} name not read", camera.EffectiveId);
            }
        });
    }

    private async Task<string?> GetTextAsync(CameraDefinition camera, string path, CancellationToken ct)
    {
        using var response = await Http().GetAsync(CameraUri(camera, path), ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
    }

    public async Task<CameraImage?> GetSnapshotAsync(string cameraId, DateTimeOffset? recordedAt = null, CancellationToken ct = default,
        CameraStreamQuality quality = CameraStreamQuality.Main)
    {
        var camera = Definition(cameraId);
        if (camera is null)
        {
            return null;
        }

        // Many viewers of one camera = one request to the camera every couple of seconds.
        if (!camera.HasSubStream)
        {
            quality = CameraStreamQuality.Main;
        }

        return await cache.GetOrCreateAsync($"camera:snapshot:{camera.EffectiveId}:{quality}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = SnapshotCacheTime;
            try
            {
                using var response = await Http().GetAsync(CameraUri(camera, HikvisionIsapi.SnapshotPath(camera.Channel, quality)), ct);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Camera {Camera} snapshot: HTTP {Status}", camera.EffectiveId, (int)response.StatusCode);
                    return null;
                }

                var type = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                return new CameraImage(await response.Content.ReadAsByteArrayAsync(ct), type.StartsWith("image/", StringComparison.Ordinal) ? type : "image/jpeg");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Camera {Camera} snapshot failed", camera.EffectiveId);
                return null;
            }
        });
    }

    public async Task<IReadOnlyList<CameraRecording>> SearchRecordingsAsync(string cameraId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken ct = default)
    {
        var camera = Definition(cameraId) ?? throw new ArgumentException($"Unknown camera '{cameraId}'.", nameof(cameraId));
        var files = await SearchFilesAsync(camera, from, to, ct);

        // Files a few seconds apart are one recording; real gaps stay – the player needs them to know where the camera
        // jumps (the timeline joins short gaps itself).
        return HikvisionIsapi.Merge(files.Select(f => f.Recording), TimeSpan.FromSeconds(5))
            .Select(r => new CameraRecording(r.Start < from ? from : r.Start, r.End > to ? to : r.End))
            .Where(r => r.End > r.Start)
            .ToList();
    }

    private async Task<IReadOnlyList<HikvisionIsapi.RecordingFile>> SearchFilesAsync(CameraDefinition camera, DateTimeOffset from,
        DateTimeOffset to, CancellationToken ct)
    {
        var zone = Options.ResolveTimeZone();
        var searchId = Guid.NewGuid();
        var found = new List<HikvisionIsapi.RecordingFile>();

        // A day of motion-triggered recording can be hundreds of files – page through, with a hard stop.
        for (var page = 0; page < 20; page++)
        {
            var body = HikvisionIsapi.BuildSearchRequest(camera.Channel, from, to, found.Count, zone, searchId);
            HikvisionIsapi.SearchPage result;
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/xml");
                using var response = await Http().PostAsync(CameraUri(camera, HikvisionIsapi.SearchPath), content, ct);
                var xml = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                {
                    throw new CameraUnavailableException($"Camera {camera.EffectiveId} recording search: HTTP {(int)response.StatusCode}.");
                }

                result = HikvisionIsapi.ParseSearchResult(xml, zone);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException && !ct.IsCancellationRequested)
            {
                throw new CameraUnavailableException($"Camera {camera.EffectiveId} recording search failed.", ex);
            }

            found.AddRange(result.Files);
            if (!result.HasMore || result.Files.Count == 0)
            {
                break;
            }
        }

        return found.OrderBy(f => f.Recording.Start).ToList();
    }

    /// <summary>
    /// The recording files of the range joined into one file. Cameras store an MPEG program stream with a Hikvision
    /// header (VLC plays it); with <see cref="CameraOptions.FfmpegPath"/> it is turned into an ordinary MP4 (no
    /// re-encoding, a few seconds).
    /// </summary>
    public async Task<CameraDownload?> DownloadAsync(string cameraId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var camera = Definition(cameraId) ?? throw new ArgumentException($"Unknown camera '{cameraId}'.", nameof(cameraId));
        var files = (await SearchFilesAsync(camera, from, to, ct))
            .Where(f => f.PlaybackUri is not null && f.Recording.End > from && f.Recording.Start < to)
            .Take(MaxDownloadFiles)
            .ToList();
        if (files.Count == 0)
        {
            return null;
        }

        var start = TimeZoneInfo.ConvertTime(files[0].Recording.Start, clock.LocalTimeZone);
        var ffmpeg = Options.FfmpegPath is { Length: > 0 } path && File.Exists(path) ? path : null;

        // Without ffmpeg the file is the cameras' own MPEG program stream – named and typed as such, not as MP4.
        var (extension, contentType) = ffmpeg is null ? (".mpg", "video/mpeg") : (".mp4", "video/mp4");
        var name = $"{FileNamePart(DisplayName(camera))}_{start:yyyy-MM-dd_HH-mm-ss}{extension}";

        return new CameraDownload(name, contentType, async (output, token) =>
        {
            if (ffmpeg is null)
            {
                await WriteFilesAsync(camera, files, output, token);
                return;
            }

            await RemuxToMp4Async(ffmpeg, camera, files, output, token);
        });
    }

    private const int MaxDownloadFiles = 400;

    private async Task WriteFilesAsync(CameraDefinition camera, IReadOnlyList<HikvisionIsapi.RecordingFile> files, Stream output,
        CancellationToken ct)
    {
        var first = true;
        foreach (var file in files)
        {
            // Older firmware takes the request only as GET with a body, newer accepts POST as well.
            using var response = await RequestFileAsync(camera, file.PlaybackUri!, HttpMethod.Get, ct) is { IsSuccessStatusCode: true } got
                ? got
                : await RequestFileAsync(camera, file.PlaybackUri!, HttpMethod.Post, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Camera {Camera} download of a recording file: HTTP {Status}", camera.EffectiveId, (int)response.StatusCode);
                continue;
            }

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var header = new byte[HikvisionIsapi.FileHeaderLength];
            var read = await body.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);

            // Only the first file keeps the Hikvision header – the next ones continue the same program stream.
            if (first || !HikvisionIsapi.HasFileHeader(header.AsSpan(0, read)))
            {
                await output.WriteAsync(header.AsMemory(0, read), ct);
            }

            await body.CopyToAsync(output, ct);
            first = false;
        }
    }

    private async Task<HttpResponseMessage> RequestFileAsync(CameraDefinition camera, string playbackUri, HttpMethod method, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, CameraUri(camera, HikvisionIsapi.DownloadPath))
        {
            Content = new StringContent(HikvisionIsapi.BuildDownloadRequest(playbackUri), Encoding.UTF8, "application/xml"),
        };
        var response = await httpFactory.CreateClient(DownloadClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode && method == HttpMethod.Get)
        {
            response.Dispose(); // the caller tries POST next
            return new HttpResponseMessage(response.StatusCode);
        }

        return response;
    }

    private async Task RemuxToMp4Async(string ffmpeg, CameraDefinition camera, IReadOnlyList<HikvisionIsapi.RecordingFile> files,
        Stream output, CancellationToken ct)
    {
        // MP4 needs a seekable output (the index goes at the start), so ffmpeg writes a temporary file.
        var temp = Path.Combine(Path.GetTempPath(), $"gaska-camera-{Guid.NewGuid():N}.mp4");
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo(ffmpeg)
            {
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-fflags", "+genpts", "-f", "mpeg", "-i", "pipe:0",
                         "-map", "0:v:0", "-c", "copy", "-movflags", "+faststart", "-y", temp })
            {
                info.ArgumentList.Add(argument);
            }

            using var process = System.Diagnostics.Process.Start(info) ?? throw new CameraUnavailableException("ffmpeg did not start.");
            var errors = process.StandardError.ReadToEndAsync(ct);
            await using (var input = process.StandardInput.BaseStream)
            {
                // ffmpeg reads the plain program stream – no Hikvision header.
                await WriteFilesAsync(camera, files, new SkipHeaderStream(input), ct);
            }

            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0 || !File.Exists(temp))
            {
                logger.LogWarning("ffmpeg failed for camera {Camera}: {Errors}", camera.EffectiveId, await errors);
                throw new CameraUnavailableException("The recording could not be converted to MP4.");
            }

            await using var result = File.OpenRead(temp);
            await result.CopyToAsync(output, ct);
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Drops the Hikvision file header from the start of what is written through it.</summary>
    private sealed class SkipHeaderStream(Stream inner) : Stream
    {
        private int _toSkip = -1;

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_toSkip < 0)
            {
                _toSkip = HikvisionIsapi.HasFileHeader(buffer.Span) ? HikvisionIsapi.FileHeaderLength : 0;
            }

            if (_toSkip > 0)
            {
                var skip = Math.Min(_toSkip, buffer.Length);
                buffer = buffer[skip..];
                _toSkip -= skip;
            }

            if (!buffer.IsEmpty)
            {
                await inner.WriteAsync(buffer, cancellationToken);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override void Flush() => inner.Flush();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static string FileNamePart(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) || c is ' ' or '#' ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "kamera" : cleaned;
    }

    public async Task<Uri> OpenLiveStreamAsync(string cameraId, CameraStreamQuality quality, CancellationToken ct = default)
    {
        var camera = Definition(cameraId) ?? throw new ArgumentException($"Unknown camera '{cameraId}'.", nameof(cameraId));
        var name = $"cam-{camera.EffectiveId}-{(quality == CameraStreamQuality.Main ? "main" : "sub")}";
        await go2rtc.EnsureStreamAsync(name, HikvisionIsapi.LiveRtspUrl(camera, quality, Options.Username, Options.Password) + LiveSourceOptions, ct);
        return go2rtc.SocketUri(name);
    }

    public async Task<CameraPlaybackStream> OpenPlaybackAsync(string cameraId, DateTimeOffset start, int speed = 1,
        CancellationToken ct = default)
    {
        var camera = Definition(cameraId) ?? throw new ArgumentException($"Unknown camera '{cameraId}'.", nameof(cameraId));
        var end = start + PlaybackLength;
        var now = clock.GetUtcNow();
        if (end > now)
        {
            end = now;
        }

        var name = $"pb-{camera.EffectiveId}-{Guid.NewGuid():N}";
        var zone = Options.ResolveTimeZone();

        // go2rtc connects to a local relay that adds the start time the camera needs in PLAY (see RtspRangeRelay).
        var relay = RtspRangeRelay.Start(camera.Host, camera.RtspPort, HikvisionIsapi.FormatRtspTime(start, zone),
            CameraPlaybackSpeeds.Normalize(speed), PlaybackLength + TimeSpan.FromMinutes(10), logger);
        _relays[name] = relay;
        try
        {
            var viaRelay = new CameraDefinition { Id = camera.EffectiveId, Host = "127.0.0.1", RtspPort = relay.Port, Channel = camera.Channel };
            var source = HikvisionIsapi.PlaybackRtspUrl(viaRelay, start, end, Options.Username, Options.Password, zone) + PlaybackSourceOptions;
            await go2rtc.EnsureStreamAsync(name, source, ct);
        }
        catch
        {
            await ReleaseRelayAsync(name);
            throw;
        }

        return new CameraPlaybackStream(name, go2rtc.SocketUri(name));
    }

    public async Task ClosePlaybackAsync(CameraPlaybackStream stream, CancellationToken ct = default)
    {
        await go2rtc.DeleteStreamAsync(stream.Name, ct);
        await ReleaseRelayAsync(stream.Name);
    }

    public async Task<bool> SetPlaybackSpeedAsync(CameraPlaybackStream stream, int speed, CancellationToken ct = default)
    {
        if (!_relays.TryGetValue(stream.Name, out var relay))
        {
            logger.LogInformation("Speed change of {Stream}: playback already closed", stream.Name);
            return false;
        }

        var changed = await relay.SetScaleAsync(CameraPlaybackSpeeds.Normalize(speed), ct);
        logger.LogInformation("Speed change of {Stream} to {Speed}x: {Result}", stream.Name, speed, changed ? "in place" : "not playing yet");
        return changed;
    }

    private async Task ReleaseRelayAsync(string name)
    {
        if (_relays.TryRemove(name, out var relay))
        {
            await relay.DisposeAsync();
        }
    }

    private CameraDefinition? Definition(string cameraId) =>
        Options.Cameras.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Host) &&
                                            string.Equals(c.EffectiveId, cameraId, StringComparison.OrdinalIgnoreCase));

    private HttpClient Http() => httpFactory.CreateClient(HttpClientName);

    private static Uri CameraUri(CameraDefinition camera, string path) => new($"http://{camera.Host}:{camera.HttpPort}{path}");
}

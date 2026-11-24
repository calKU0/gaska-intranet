using System.Buffers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Gaska.Intranet.Core.Auditing;
using Gaska.Intranet.Core.Cameras;
using Gaska.Intranet.Core.Security;
using Gaska.Intranet.Web.Modules;
using Gaska.Intranet.Web.Security;
using Microsoft.Extensions.Caching.Memory;

namespace Gaska.Intranet.Web.Components.Pages.Cameras;

/// <summary>
/// Camera endpoints. The browser never talks to a camera or to go2rtc: pictures and video sockets go through the
/// application, which checks the location permission on every request and adds the camera credentials itself.
/// </summary>
public static class CameraEndpoints
{
    public static string SnapshotUrl(string cameraId, CameraStreamQuality quality = CameraStreamQuality.Sub) =>
        $"/kamery/{Uri.EscapeDataString(cameraId)}/obraz" + (quality == CameraStreamQuality.Main ? "?jakosc=main" : string.Empty);

    /// <summary>The recording files from <paramref name="from"/> to <paramref name="to"/> as one file to save.</summary>
    public static string DownloadUrl(string cameraId, DateTimeOffset from, DateTimeOffset to) =>
        $"/kamery/{Uri.EscapeDataString(cameraId)}/pobierz?od={from.ToUnixTimeSeconds()}&do={to.ToUnixTimeSeconds()}";

    /// <summary>Longest range of one download – the cameras send whole files, a long range is a large file.</summary>
    public static readonly TimeSpan MaxDownload = TimeSpan.FromHours(1);

    public static string LiveSocketUrl(string cameraId, CameraStreamQuality quality) =>
        $"/kamery/strumien/{Uri.EscapeDataString(cameraId)}" + (quality == CameraStreamQuality.Main ? "?jakosc=main" : string.Empty);

    public static string PlaybackSocketUrl(string ticket) => $"/kamery/nagranie/{ticket}";

    /// <summary>The same video as plain HTTP (fragmented MP4) – for networks where WebSockets do not get through.</summary>
    public static string LiveVideoUrl(string cameraId, CameraStreamQuality quality) =>
        $"/kamery/{Uri.EscapeDataString(cameraId)}/wideo" + (quality == CameraStreamQuality.Main ? "?jakosc=main" : string.Empty);

    public static string PlaybackVideoUrl(string ticket) => $"/kamery/nagranie/{ticket}/wideo";

    /// <summary>HttpClient for the go2rtc MP4 streams – no timeout, a stream lasts as long as someone watches.</summary>
    public const string StreamClientName = "go2rtc-stream";

    /// <summary>Live sockets are closed after this time – the player reconnects, so permissions are re-checked.</summary>
    private static readonly TimeSpan MaxLiveSession = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan MaxPlaybackSession = TimeSpan.FromHours(2);

    /// <summary>The only requests a viewer may send to go2rtc (MSE / MP4 / MJPEG players) – no WebRTC, no other commands.</summary>
    private static readonly HashSet<string> AllowedClientMessages = new(StringComparer.Ordinal) { "mse", "mp4", "mjpeg" };

    public static IEndpointRouteBuilder MapCameraEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/kamery").RequireAuthorization(ModulePolicy.Prefix + ModuleKeys.Cameras);

        // Current frame – tile poster, demo player and fallback when the video socket fails. "czas" = demo recordings.
        group.MapGet("/{cameraId}/obraz", async (string cameraId, long? czas, string? jakosc, HttpContext http, ICameraAccess access,
            ICameraService cameras, CancellationToken ct) =>
        {
            var camera = await access.GetViewableCameraAsync(http.User, cameraId, ct);
            if (camera is null)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            DateTimeOffset? recordedAt = czas is > 0 ? DateTimeOffset.FromUnixTimeSeconds(czas.Value) : null;
            var image = await cameras.GetSnapshotAsync(camera.Id, recordedAt, ct, Quality(jakosc));
            http.Response.Headers.CacheControl = "no-store";
            return image is null ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable) : Results.File(image.Content, image.ContentType);
        });

        group.Map("/strumien/{cameraId}", async (string cameraId, string? jakosc, HttpContext http, ICameraAccess access,
            ICameraService cameras, ILoggerFactory logs) =>
        {
            if (RejectSocket(http, logs.CreateLogger(typeof(CameraEndpoints))) is { } rejected)
            {
                return rejected;
            }

            var camera = await access.GetViewableCameraAsync(http.User, cameraId, http.RequestAborted);
            if (camera is null)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var quality = Quality(jakosc);
            var logger = logs.CreateLogger(typeof(CameraEndpoints));
            Uri upstream;
            try
            {
                upstream = await cameras.OpenLiveStreamAsync(camera.Id, quality, http.RequestAborted);
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
            {
                return Results.Empty; // The viewer left (tile scrolled away, page closed) before the stream was ready.
            }
            catch (Exception ex) when (ex is CameraUnavailableException or HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Live stream of camera {Camera} unavailable", camera.Id);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            await ProxyAsync(http, upstream, MaxLiveSession, logger);
            return Results.Empty;
        });

        // Live video over plain HTTP: the player's fallback when the WebSocket does not work (IIS without the
        // "WebSocket Protocol" feature, a proxy that strips the upgrade).
        group.MapGet("/{cameraId}/wideo", async (string cameraId, string? jakosc, HttpContext http, ICameraAccess access,
            ICameraService cameras, IHttpClientFactory httpFactory, ILoggerFactory logs) =>
        {
            var camera = await access.GetViewableCameraAsync(http.User, cameraId, http.RequestAborted);
            if (camera is null)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var logger = logs.CreateLogger(typeof(CameraEndpoints));
            Uri socket;
            try
            {
                socket = await cameras.OpenLiveStreamAsync(camera.Id, Quality(jakosc), http.RequestAborted);
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
            {
                return Results.Empty;
            }
            catch (Exception ex) when (ex is CameraUnavailableException or HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Live stream of camera {Camera} unavailable", camera.Id);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            await StreamMp4Async(http, socket, MaxLiveSession, httpFactory, logger);
            return Results.Empty;
        });

        // Recording files of a range, joined into one file to save (MP4 when ffmpeg is configured).
        group.MapGet("/{cameraId}/pobierz", async (string cameraId, long od, long @do, HttpContext http, ICameraAccess access,
            ICameraService cameras, IAuditLog audit, ILoggerFactory logs) =>
        {
            var camera = await access.GetViewableCameraAsync(http.User, cameraId, http.RequestAborted);
            if (camera is null)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var from = DateTimeOffset.FromUnixTimeSeconds(od);
            var to = DateTimeOffset.FromUnixTimeSeconds(@do);
            if (to <= from || to - from > MaxDownload)
            {
                return Results.BadRequest();
            }

            var logger = logs.CreateLogger(typeof(CameraEndpoints));
            CameraDownload? download;
            try
            {
                download = await cameras.DownloadAsync(camera.Id, from, to, http.RequestAborted);
            }
            catch (Exception ex) when (ex is CameraUnavailableException or HttpRequestException or TaskCanceledException
                                           && !http.RequestAborted.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Recording download of camera {Camera} unavailable", camera.Id);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (download is null)
            {
                return Results.NotFound();
            }

            await audit.WriteAsync(AuditEvents.CameraDownload, http.User.Identity?.Name,
                $"{camera.Name} ({camera.Id}) {from.ToLocalTime():yyyy-MM-dd HH:mm}–{to.ToLocalTime():HH:mm}", http.RequestAborted);

            http.Response.ContentType = download.ContentType;
            // filename* keeps Polish letters of the camera name.
            var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
            disposition.SetHttpFileName(download.FileName);
            http.Response.Headers.ContentDisposition = disposition.ToString();
            http.Response.Headers.CacheControl = "no-store";
            http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
            try
            {
                await download.WriteToAsync(http.Response.Body, http.RequestAborted);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException or CameraUnavailableException)
            {
                logger.LogWarning(ex, "Recording download of camera {Camera} interrupted", camera.Id);
            }

            return Results.Empty;
        });

        group.Map("/nagranie/{ticket}", async (string ticket, HttpContext http, ICameraAccess access, ICameraService cameras,
            CameraPlaybackTickets tickets, IAuditLog audit, ILoggerFactory logs) =>
        {
            var logger = logs.CreateLogger(typeof(CameraEndpoints));
            if (RejectSocket(http, logger) is { } rejected)
            {
                return rejected;
            }

            var opened = await OpenPlaybackAsync(ticket, http, access, cameras, tickets, audit, logger);
            if (opened.Error is { } error)
            {
                return error;
            }

            try
            {
                await ProxyAsync(http, opened.Stream!.SocketUri, MaxPlaybackSession, logger);
            }
            finally
            {
                await cameras.ClosePlaybackAsync(opened.Stream!, CancellationToken.None);
            }

            return Results.Empty;
        });

        group.MapGet("/nagranie/{ticket}/wideo", async (string ticket, HttpContext http, ICameraAccess access, ICameraService cameras,
            CameraPlaybackTickets tickets, IAuditLog audit, IHttpClientFactory httpFactory, ILoggerFactory logs) =>
        {
            var logger = logs.CreateLogger(typeof(CameraEndpoints));
            var opened = await OpenPlaybackAsync(ticket, http, access, cameras, tickets, audit, logger);
            if (opened.Error is { } error)
            {
                return error;
            }

            try
            {
                await StreamMp4Async(http, opened.Stream!.SocketUri, MaxPlaybackSession, httpFactory, logger);
            }
            finally
            {
                await cameras.ClosePlaybackAsync(opened.Stream!, CancellationToken.None);
            }

            return Results.Empty;
        });

        return endpoints;
    }

    private static CameraStreamQuality Quality(string? jakosc) =>
        string.Equals(jakosc, "main", StringComparison.OrdinalIgnoreCase) ? CameraStreamQuality.Main : CameraStreamQuality.Sub;

    /// <summary>
    /// A video socket must be a WebSocket handshake from a page of the intranet itself (blocks cross-site WebSocket
    /// hijacking – browsers always send Origin on WebSocket handshakes). The reason of a refusal goes to the log: the
    /// player then falls back to HTTP video, and the log says why the WebSocket did not work.
    /// </summary>
    private static IResult? RejectSocket(HttpContext http, ILogger logger)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            logger.LogWarning(
                "Camera video {Path} arrived without a WebSocket upgrade – on IIS install the \"WebSocket Protocol\" feature, " +
                "or a proxy between the browser and the server strips it. The player uses HTTP video instead.", http.Request.Path);
            return Results.BadRequest();
        }

        var origin = http.Request.Headers.Origin.ToString();
        if (Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Authority, http.Request.Host.Value, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        logger.LogWarning("Camera video socket refused: origin {Origin} does not match host {Host}", origin, http.Request.Host.Value);
        return Results.BadRequest();
    }

    private sealed record OpenedPlayback(CameraPlaybackStream? Stream, IResult? Error);

    /// <summary>Checks the ticket and the permission, starts the playback and writes the audit entry (once per ticket).</summary>
    private static async Task<OpenedPlayback> OpenPlaybackAsync(string ticket, HttpContext http, ICameraAccess access,
        ICameraService cameras, CameraPlaybackTickets tickets, IAuditLog audit, ILogger logger)
    {
        // The ticket is bound to the account that asked for it; the permission is checked again on every connection.
        var request = tickets.Use(ticket, http.User.GetSid(), out var firstUse);
        var camera = request is null ? null : await access.GetViewableCameraAsync(http.User, request.CameraId, http.RequestAborted);
        if (request is null || camera is null)
        {
            return new OpenedPlayback(null, Results.StatusCode(StatusCodes.Status403Forbidden));
        }

        CameraPlaybackStream stream;
        try
        {
            stream = await cameras.OpenPlaybackAsync(camera.Id, request.Start, request.Speed, http.RequestAborted);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            return new OpenedPlayback(null, Results.Empty);
        }
        catch (Exception ex) when (ex is CameraUnavailableException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Playback of camera {Camera} unavailable", camera.Id);
            return new OpenedPlayback(null, Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        }

        request.Stream = stream;
        if (firstUse)
        {
            await audit.WriteAsync(AuditEvents.CameraPlayback, http.User.Identity?.Name,
                $"{camera.Name} ({camera.Id}) od {request.Start.ToLocalTime():yyyy-MM-dd HH:mm:ss}", http.RequestAborted);
        }

        return new OpenedPlayback(stream, null);
    }

    /// <summary>
    /// go2rtc's fragmented MP4 stream of the same source (<c>/api/stream.mp4</c>) copied to the browser as it comes –
    /// a &lt;video&gt; plays it without WebSockets or Media Source Extensions.
    /// </summary>
    private static async Task StreamMp4Async(HttpContext http, Uri socketUri, TimeSpan maxDuration, IHttpClientFactory httpFactory,
        ILogger logger)
    {
        var mp4 = new UriBuilder(socketUri)
        {
            Scheme = socketUri.Scheme == "wss" ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
            Path = socketUri.AbsolutePath.Replace("/api/ws", "/api/stream.mp4", StringComparison.Ordinal),
        }.Uri;

        using var session = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        session.CancelAfter(maxDuration);
        try
        {
            using var response = await httpFactory.CreateClient(StreamClientName)
                .GetAsync(mp4, HttpCompletionOption.ResponseHeadersRead, session.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("go2rtc MP4 stream: HTTP {Status}", (int)response.StatusCode);
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            http.Response.ContentType = "video/mp4";
            http.Response.Headers.CacheControl = "no-store";
            http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
            await using var body = await response.Content.ReadAsStreamAsync(session.Token);
            await body.CopyToAsync(http.Response.Body, session.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException)
        {
            // Viewer left, time limit reached or go2rtc went away – the player reconnects if it still needs the video.
            if (!http.Response.HasStarted)
            {
                logger.LogWarning(ex, "go2rtc MP4 stream unavailable");
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            }
        }
    }

    private static async Task ProxyAsync(HttpContext http, Uri upstreamUri, TimeSpan maxDuration, ILogger logger)
    {
        using var upstream = new ClientWebSocket();
        upstream.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await upstream.ConnectAsync(upstreamUri, connect.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                logger.LogWarning(ex, "go2rtc socket unavailable");
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
        }

        using var client = await http.WebSockets.AcceptWebSocketAsync();
        using var session = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        session.CancelAfter(maxDuration);

        var toCamera = PumpFromClientAsync(client, upstream, session.Token);
        var toBrowser = PumpAsync(upstream, client, session.Token);
        await Task.WhenAny(toCamera, toBrowser);
        await session.CancelAsync();
        await Task.WhenAll(Quietly(toCamera), Quietly(toBrowser));

        await CloseQuietlyAsync(client);
        await CloseQuietlyAsync(upstream);
    }

    /// <summary>Video frames go2rtc → browser, as they come.</summary>
    private static async Task PumpAsync(WebSocket from, WebSocket to, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await from.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                await to.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Player requests browser → go2rtc: small JSON messages of the allowed types only.</summary>
    private static async Task PumpFromClientAsync(WebSocket client, WebSocket upstream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (!ct.IsCancellationRequested)
        {
            var length = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                if (length == buffer.Length)
                {
                    return; // Oversized message – not a player request.
                }

                result = await client.ReceiveAsync(buffer.AsMemory(length), ct);
                length += result.Count;
            }
            while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            if (result.MessageType != WebSocketMessageType.Text || !IsAllowedClientMessage(buffer.AsSpan(0, length)))
            {
                continue;
            }

            await upstream.SendAsync(buffer.AsMemory(0, length), WebSocketMessageType.Text, true, ct);
        }
    }

    private static bool IsAllowedClientMessage(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            using var doc = JsonDocument.ParseValue(ref reader);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String &&
                AllowedClientMessages.Contains(type.GetString()!);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // Viewer closed the page / network dropped – normal end of a stream.
        }
    }

    private static async Task CloseQuietlyAsync(WebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
    }
}

/// <summary>A playback request prepared by the page: which camera, from when, how fast, for whom.</summary>
public sealed record CameraPlaybackRequest(string Sid, string CameraId, DateTimeOffset Start)
{
    /// <summary>Set by the first socket – the audit log gets one entry per playback, not one per reconnect.</summary>
    internal bool Used { get; set; }

    /// <summary>Current speed – changed in place while the recording plays.</summary>
    public int Speed { get; set; } = 1;

    /// <summary>The playback stream while a player is connected – the page changes its speed through it.</summary>
    public CameraPlaybackStream? Stream { get; set; }
}

/// <summary>
/// Tickets for playback sockets: the page (Blazor circuit) checks the permission and picks the time, the socket only
/// presents the ticket – so the start time is not a free URL parameter. A ticket belongs to one account and lives while
/// the player uses it (reconnects after a hidden tab or a network drop, however long the playback ran) – at most a
/// few hours.
/// </summary>
public sealed class CameraPlaybackTickets(IMemoryCache cache)
{
    public string Create(string sid, string cameraId, DateTimeOffset start, int speed = 1)
    {
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        cache.Set(Key(ticket), new CameraPlaybackRequest(sid, cameraId, start) { Speed = CameraPlaybackSpeeds.Normalize(speed) }, new MemoryCacheEntryOptions
        {
            // No sliding expiration: a long playback does not touch the ticket, and must still reconnect.
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(3),
        });
        return ticket;
    }

    /// <summary>The request when the ticket exists and belongs to <paramref name="sid"/>; <paramref name="firstUse"/> – not used before.</summary>
    public CameraPlaybackRequest? Use(string ticket, string sid, out bool firstUse)
    {
        firstUse = false;
        if (ticket.Length > 64 || sid.Length == 0 || !cache.TryGetValue(Key(ticket), out CameraPlaybackRequest? request) || request is null ||
            !string.Equals(request.Sid, sid, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        lock (request)
        {
            firstUse = !request.Used;
            request.Used = true;
        }

        return request;
    }

    /// <summary>The request of a ticket of this account (the page asking for a speed change); null when unknown.</summary>
    public CameraPlaybackRequest? Find(string ticket, string sid) =>
        cache.TryGetValue(Key(ticket), out CameraPlaybackRequest? request) && request is not null &&
        string.Equals(request.Sid, sid, StringComparison.OrdinalIgnoreCase) && sid.Length > 0 ? request : null;

    private static string Key(string ticket) => "camera:playback:" + ticket;
}

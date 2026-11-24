using Gaska.Intranet.Core.Cameras;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Infrastructure.Cameras;

/// <summary>
/// go2rtc REST API (localhost only). Streams are registered by the application, so camera passwords live in
/// appsettings.Local.json and go2rtc's own config file stays free of them.
/// </summary>
internal sealed class Go2RtcClient(IHttpClientFactory httpFactory, IOptions<CameraOptions> options, ILogger<Go2RtcClient> logger)
{
    private HttpClient Http => httpFactory.CreateClient(HttpClientName);

    public const string HttpClientName = "go2rtc";

    private Uri BaseUri => new(options.Value.Go2RtcUrl.TrimEnd('/') + "/");

    /// <summary>Creates the stream, or keeps it when it already exists with the same source (PATCH).</summary>
    public async Task EnsureStreamAsync(string name, string source, CancellationToken ct)
    {
        using var patch = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Patch, StreamsUri(name, source)), ct);
        if (patch.IsSuccessStatusCode)
        {
            return;
        }

        // Older go2rtc versions: PATCH is "update only" – create with PUT.
        using var put = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Put, StreamsUri(name, source)), ct);
        if (!put.IsSuccessStatusCode)
        {
            throw new CameraUnavailableException($"go2rtc refused stream '{name}' ({(int)put.StatusCode}).");
        }
    }

    public async Task DeleteStreamAsync(string name, CancellationToken ct)
    {
        try
        {
            using var response = await Http.DeleteAsync(new Uri(BaseUri, $"api/streams?src={Uri.EscapeDataString(name)}"), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not remove go2rtc stream {Stream}", name);
        }
    }

    /// <summary>The MSE / MJPEG WebSocket of a stream – proxied by the application, never given to the browser.</summary>
    public Uri SocketUri(string name)
    {
        var builder = new UriBuilder(new Uri(BaseUri, "api/ws"))
        {
            Query = "src=" + Uri.EscapeDataString(name),
        };
        builder.Scheme = builder.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        return builder.Uri;
    }

    private Uri StreamsUri(string name, string source) =>
        new(BaseUri, $"api/streams?name={Uri.EscapeDataString(name)}&src={Uri.EscapeDataString(source)}");
}


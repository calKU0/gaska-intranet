using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Gaska.Intranet.Infrastructure.Cameras;

/// <summary>
/// A one-recording RTSP relay on localhost between go2rtc and a camera. Hikvision cameras start a playback at the time in
/// the PLAY request's <c>Range: clock=…</c> header – without it they play from the oldest recording on the storage,
/// whatever <c>starttime</c> says in the URL. go2rtc sends PLAY without any Range header, so the relay adds it (and
/// <c>Scale</c> for a faster playback). The speed can also change while the recording plays: the relay sends the camera
/// one more PLAY with the new Scale in the same session (no new Range – the camera goes on from where it is) and hides
/// the camera's answer from go2rtc. Every other byte (requests, digest authentication, the RTP/RTCP data) passes through.
/// </summary>
internal sealed class RtspRangeRelay : IAsyncDisposable
{
    /// <summary>CSeq of the relay's own requests – far above go2rtc's, so their answers can be told apart and dropped.</summary>
    private const int OwnCSeqBase = 90000;

    private readonly TcpListener _listener;
    private readonly string _cameraHost;
    private readonly int _cameraPort;
    private readonly string _range;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private volatile int _scale;
    private volatile Connection? _active;
    private int _ownCSeq = OwnCSeqBase;

    private RtspRangeRelay(string cameraHost, int cameraPort, string range, int scale, TimeSpan maxLifetime, ILogger logger)
    {
        _scale = scale;
        _cameraHost = cameraHost;
        _cameraPort = cameraPort;
        _range = range;
        _logger = logger;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _stop.CancelAfter(maxLifetime); // Safety net when the viewer's socket never reports the end.
        _acceptLoop = AcceptLoopAsync();
    }

    /// <summary>Port on 127.0.0.1 to give go2rtc instead of the camera's RTSP port.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <param name="clockStart">Start time in the camera's clock format, e.g. <c>20261124T094258Z</c>.</param>
    /// <param name="scale">Playback speed – above 1 the camera sends the recording that many times faster (<c>Scale</c>).</param>
    public static RtspRangeRelay Start(string cameraHost, int cameraPort, string clockStart, int scale, TimeSpan maxLifetime, ILogger logger) =>
        new(cameraHost, cameraPort, $"clock={clockStart}-", scale, maxLifetime, logger);

    /// <summary>
    /// Changes the speed of the playing recording without a new connection. False when nothing plays yet (the new speed
    /// is still used when the playback starts).
    /// </summary>
    public async Task<bool> SetScaleAsync(int scale, CancellationToken ct)
    {
        _scale = scale;
        if (_active is not { LastPlay: { } play } connection)
        {
            return false;
        }

        var cseq = Interlocked.Increment(ref _ownCSeq);
        var request = SpeedRequest(play, scale, cseq);
        await connection.WriteAsync(Encoding.ASCII.GetBytes(request + "\r\n\r\n"), ct);
        return true;
    }

    /// <summary>go2rtc's PLAY turned into a speed change: own CSeq, no Range (go on from here), the new Scale.</summary>
    internal static string SpeedRequest(string playHeader, int scale, int cseq)
    {
        var lines = playHeader.Split("\r\n")
            .Where(l => !l.StartsWith("Range:", StringComparison.OrdinalIgnoreCase) &&
                        !l.StartsWith("Scale:", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.StartsWith("CSeq:", StringComparison.OrdinalIgnoreCase) ? $"CSeq: {cseq}" : l);
        return string.Join("\r\n", lines) + $"\r\nScale: {scale}.0";
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = RelayAsync(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task RelayAsync(TcpClient client)
    {
        using var camera = new TcpClient();
        using (client)
        {
            try
            {
                await camera.ConnectAsync(_cameraHost, _cameraPort, _stop.Token);
                var connection = new Connection(camera.GetStream());
                _active = connection;
                using var session = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var up = PumpRequestsAsync(client.GetStream(), connection, session.Token);
                var down = PumpResponsesAsync(camera.GetStream(), client.GetStream(), session.Token);
                await Task.WhenAny(up, down);
                await session.CancelAsync();
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                _logger.LogDebug(ex, "RTSP relay to {Camera} ended", _cameraHost);
            }
        }
    }

    /// <summary>go2rtc → camera: RTSP requests (PLAY gets the Range header) and interleaved RTCP frames ('$' + channel + length).</summary>
    private async Task PumpRequestsAsync(Stream from, Connection to, CancellationToken ct)
    {
        await PumpMessagesAsync(from, ct, async message =>
        {
            if (message.Header is { } header)
            {
                if (header.StartsWith("PLAY ", StringComparison.Ordinal))
                {
                    to.LastPlay = header;
                }

                await to.WriteAsync(Compose(AddRange(header, _range, _scale), message.Body), ct);
                return;
            }

            await to.WriteAsync(message.Frame, ct);
        });
    }

    /// <summary>Camera → go2rtc: everything except the answers to the relay's own requests.</summary>
    private static async Task PumpResponsesAsync(Stream from, Stream to, CancellationToken ct)
    {
        await PumpMessagesAsync(from, ct, async message =>
        {
            if (message.Header is { } header)
            {
                if (CSeqOf(header) >= OwnCSeqBase)
                {
                    return;
                }

                await to.WriteAsync(Compose(header, message.Body), ct);
                return;
            }

            await to.WriteAsync(message.Frame, ct);
        });
    }

    private static async Task PumpMessagesAsync(Stream from, CancellationToken ct, Func<RtspMessage, Task> handle)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (!ct.IsCancellationRequested)
        {
            var read = await from.ReadAsync(chunk, ct);
            if (read == 0)
            {
                return;
            }

            buffer.Write(chunk, 0, read);
            var data = buffer.GetBuffer();
            var length = (int)buffer.Length;
            var offset = 0;
            while (offset < length)
            {
                var consumed = TryTakeMessage(data.AsSpan(offset, length - offset), out var message);
                if (consumed == 0)
                {
                    break;
                }

                await handle(message);
                offset += consumed;
            }

            // Keep the incomplete tail for the next read.
            var rest = data.AsSpan(offset, length - offset).ToArray();
            buffer.SetLength(0);
            buffer.Write(rest);
        }
    }

    /// <summary>An interleaved frame (<see cref="Frame"/>) or an RTSP request / response (<see cref="Header"/> + body).</summary>
    internal readonly record struct RtspMessage(ReadOnlyMemory<byte> Frame, string? Header, ReadOnlyMemory<byte> Body);

    /// <summary>One complete message from the start of <paramref name="data"/>; returns the bytes consumed (0 = incomplete).</summary>
    internal static int TryTakeMessage(ReadOnlySpan<byte> data, out RtspMessage message)
    {
        message = default;
        if (data.IsEmpty)
        {
            return 0;
        }

        if (data[0] == (byte)'$')
        {
            if (data.Length < 4)
            {
                return 0;
            }

            var size = 4 + ((data[2] << 8) | data[3]);
            if (data.Length < size)
            {
                return 0;
            }

            message = new RtspMessage(data[..size].ToArray(), null, default);
            return size;
        }

        var headerEnd = data.IndexOf("\r\n\r\n"u8);
        if (headerEnd < 0)
        {
            return 0;
        }

        var header = Encoding.ASCII.GetString(data[..headerEnd]);
        var bodyLength = ContentLength(header);
        var total = headerEnd + 4 + bodyLength;
        if (data.Length < total)
        {
            return 0;
        }

        message = new RtspMessage(default, header, data.Slice(headerEnd + 4, bodyLength).ToArray());
        return total;
    }

    private static byte[] Compose(string header, ReadOnlyMemory<byte> body)
    {
        var bytes = new byte[header.Length + 4 + body.Length];
        Encoding.ASCII.GetBytes(header, bytes);
        "\r\n\r\n"u8.CopyTo(bytes.AsSpan(header.Length));
        body.Span.CopyTo(bytes.AsSpan(header.Length + 4));
        return bytes;
    }

    /// <summary>
    /// Adds <c>Range</c> (and <c>Scale</c> for a faster playback) to a PLAY request that has none; any other request is
    /// returned unchanged.
    /// </summary>
    internal static string AddRange(string header, string range, int scale = 1)
    {
        if (!header.StartsWith("PLAY ", StringComparison.Ordinal) ||
            header.Contains("\r\nRange:", StringComparison.OrdinalIgnoreCase))
        {
            return header;
        }

        return header + "\r\nRange: " + range + (scale > 1 ? $"\r\nScale: {scale}.0" : string.Empty);
    }

    internal static int CSeqOf(string header) => HeaderValue(header, "CSeq:") is { } value && int.TryParse(value, out var cseq) ? cseq : 0;

    private static int ContentLength(string header) =>
        HeaderValue(header, "Content-Length:") is { } value && int.TryParse(value, out var length) && length >= 0 ? length : 0;

    private static string? HeaderValue(string header, string name)
    {
        foreach (var line in header.Split("\r\n"))
        {
            if (line.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                return line[name.Length..].Trim();
            }
        }

        return null;
    }

    /// <summary>The camera side of one go2rtc connection: writes from go2rtc and from the relay do not interleave.</summary>
    private sealed class Connection(Stream camera)
    {
        private readonly SemaphoreSlim _write = new(1, 1);

        /// <summary>go2rtc's last PLAY (before the relay's additions) – the template of a speed change.</summary>
        public volatile string? LastPlay;

        public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            await _write.WaitAsync(ct);
            try
            {
                await camera.WriteAsync(data, ct);
            }
            finally
            {
                _write.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }

        _stop.Dispose();
    }
}

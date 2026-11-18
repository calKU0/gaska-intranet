namespace Gaska.Intranet.Web.Configuration;

/// <summary>
/// Warns in the log when browsers talk to the interactive pages without WebSockets. Blazor then falls back to long
/// polling (or server-sent events): every click waits for the next poll, pages react with a delay and the connection
/// drops more often ("Brak połączenia z serwerem"). On IIS the cause is a missing "WebSocket Protocol" feature
/// (Server Manager → Web Server → Application Development) or a proxy in between that strips the upgrade.
/// </summary>
public sealed class BlazorTransportMonitor(RequestDelegate next, ILogger<BlazorTransportMonitor> logger)
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private static long _lastWarningTicks;

    public Task InvokeAsync(HttpContext context)
    {
        // Long polling and SSE read the circuit with "GET /_blazor?id=…" that is not a WebSocket upgrade.
        if (HttpMethods.IsGet(context.Request.Method)
            && context.Request.Path.Equals("/_blazor", StringComparison.OrdinalIgnoreCase)
            && context.Request.Query.ContainsKey("id")
            && !context.WebSockets.IsWebSocketRequest)
        {
            var now = DateTime.UtcNow.Ticks;
            var last = Interlocked.Read(ref _lastWarningTicks);
            if (now - last > Interval.Ticks && Interlocked.CompareExchange(ref _lastWarningTicks, now, last) == last)
            {
                var transport = context.Request.Headers.Accept.ToString().Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)
                    ? "Server-Sent Events" : "long polling";
                logger.LogWarning(
                    "Blazor connection without WebSockets ({Transport}) from {Client} – pages respond slowly and disconnect more often. " +
                    "On IIS install the \"WebSocket Protocol\" feature; check proxies between the browser and the server.",
                    transport, context.Connection.RemoteIpAddress);
            }
        }

        return next(context);
    }
}

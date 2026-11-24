using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gaska.Intranet.Infrastructure.Cameras;

/// <summary>
/// Reads the camera names from the cameras' web panels at startup and then every few hours, so a name changed on a
/// camera shows up in the intranet without editing the configuration.
/// </summary>
internal sealed class CameraNameRefresher(HikvisionCameraService cameras, ILogger<CameraNameRefresher> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await cameras.RefreshNamesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Reading camera names failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

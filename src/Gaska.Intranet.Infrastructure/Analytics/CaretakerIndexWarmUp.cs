using Gaska.Intranet.Core.Crm;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gaska.Intranet.Infrastructure.Analytics;

/// <summary>
/// Builds the caretaker index right after the application starts, so the first person filtering the contractor list
/// by caretakers does not wait for all contractors to be read.
/// </summary>
internal sealed class CaretakerIndexWarmUp(IContractorCaretakerService caretakers, ILogger<CaretakerIndexWarmUp> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            await caretakers.GetNamesAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // The application is stopping.
        }
        catch (Exception ex)
        {
            // GoNet or the ERP unavailable – the index is built on first use instead.
            logger.LogWarning(ex, "Caretaker index not built at start");
        }
    }
}

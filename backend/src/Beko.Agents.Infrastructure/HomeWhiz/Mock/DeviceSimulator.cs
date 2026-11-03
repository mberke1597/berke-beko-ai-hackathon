using Beko.Agents.Application.Common;
using Beko.Agents.Application.Homes;
using Beko.Agents.Application.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Beko.Agents.Infrastructure.HomeWhiz.Mock;

/// <summary>
/// Ticks the mock HomeWhiz every <see cref="SimulationOptions.TickSeconds"/> and pushes visible changes and appliance
/// events to clients. This stands in for the push channel the real HomeWhiz would offer.
/// </summary>
public sealed partial class DeviceSimulator(
    MockHomeWhizStore store,
    IDeviceNotifier notifier,
    IOptions<SimulationOptions> options,
    TimeProvider timeProvider,
    ILogger<DeviceSimulator> logger,
    IServiceProvider? services = null) : BackgroundService
{
    /// <summary>Runs one simulation step and publishes its results.</summary>
    public async Task TickOnceAsync(CancellationToken cancellationToken)
    {
        var result = store.Tick();
        foreach (var appliance in result.Changed)
        {
            await notifier.ApplianceChangedAsync(appliance, cancellationToken);
        }

        foreach (var applianceEvent in result.Events)
        {
            await notifier.ApplianceEventAsync(applianceEvent.HomeId, applianceEvent.ApplianceId, applianceEvent.Event, applianceEvent.AtUtc, cancellationToken);
            // Resolved per event, not at construction: observers reach HomeWhiz over HTTP and must not be built while the host starts.
            foreach (var observer in services?.GetServices<IApplianceEventObserver>() ?? [])
            {
                try
                {
                    await observer.OnApplianceEventAsync(applianceEvent.HomeId, applianceEvent.ApplianceId, applianceEvent.Event, applianceEvent.AtUtc, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogObserverFailed(logger, ex, observer.GetType().Name);
                }
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.TickSeconds), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogTickFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Appliance event observer {Observer} failed")]
    private static partial void LogObserverFailed(ILogger logger, Exception exception, string observer);

    [LoggerMessage(Level = LogLevel.Error, Message = "Device simulator tick failed")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);
}

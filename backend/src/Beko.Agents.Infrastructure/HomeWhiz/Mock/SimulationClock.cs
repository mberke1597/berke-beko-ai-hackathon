using Beko.Agents.Application.Options;
using Microsoft.Extensions.Options;

namespace Beko.Agents.Infrastructure.HomeWhiz.Mock;

/// <summary>
/// Simulated time of the mock HomeWhiz world: starts at the real time when the app starts and runs
/// <see cref="SimulationOptions.TimeScale"/> times faster.
/// </summary>
public sealed class SimulationClock
{
    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _realStart;
    private readonly double _timeScale;

    public SimulationClock(TimeProvider timeProvider, IOptions<SimulationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);

        _timeProvider = timeProvider;
        _realStart = timeProvider.GetUtcNow();
        _timeScale = options.Value.TimeScale;
    }

    public DateTime UtcNow => (_realStart + ((_timeProvider.GetUtcNow() - _realStart) * _timeScale)).UtcDateTime;
}

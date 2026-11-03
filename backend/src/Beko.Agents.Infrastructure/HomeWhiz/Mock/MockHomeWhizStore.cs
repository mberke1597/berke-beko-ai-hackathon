using Beko.Agents.Application.Homes;
using Beko.Agents.Application.Options;
using Beko.Agents.Domain.Homes;
using Microsoft.Extensions.Options;

namespace Beko.Agents.Infrastructure.HomeWhiz.Mock;

/// <summary>
/// In-memory state of the mock HomeWhiz (resets on restart). Holds the precise simulated state, serves the rounded
/// visible state, applies commands and keeps a downsampled telemetry ring per appliance. Thread-safe.
/// </summary>
public sealed class MockHomeWhizStore
{
    /// <summary>Simulated time between telemetry samples.</summary>
    public static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(5);

    /// <summary>Simulated history kept per appliance (two days at <see cref="SampleInterval"/>).</summary>
    public static readonly TimeSpan TelemetryRetention = TimeSpan.FromDays(2);

    /// <summary>Default telemetry window when no range is given.</summary>
    public static readonly TimeSpan DefaultTelemetryWindow = TimeSpan.FromHours(24);

    private static readonly int TelemetryCapacity = (int)(TelemetryRetention / SampleInterval);

    private readonly Lock _gate = new();
    private readonly SimulationClock _clock;
    private readonly double _outdoorTempC;
    private readonly Dictionary<string, Entry> _entries;
    private readonly List<string> _order;
    private DateTime _lastTickUtc;
    private DateTime _lastSampleUtc;

    public MockHomeWhizStore(SimulationClock clock, IOptions<SimulationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);

        _clock = clock;
        _outdoorTempC = options.Value.OutdoorTempC;
        var now = clock.UtcNow;
        var appliances = DemoHomeSeed.CreateAppliances(now);
        _order = [.. appliances.Select(a => a.Id)];
        _entries = appliances.ToDictionary(a => a.Id, a => new Entry(a, BackfillTelemetry(a, now)), StringComparer.Ordinal);
        _lastTickUtc = now;
        _lastSampleUtc = now;
    }

    public DateTime UtcNow => _clock.UtcNow;

    public static IReadOnlyList<HomeSummary> GetHomes() =>
        [new HomeSummary(DemoHomeSeed.Home.Id, DemoHomeSeed.Home.Name, DemoHomeSeed.Home.City)];

    public static HomeDetails? GetHome(string homeId) =>
        IsKnownHome(homeId)
            ? new HomeDetails(DemoHomeSeed.Home.Id, DemoHomeSeed.Home.Name, DemoHomeSeed.Home.City, DemoHomeSeed.Home.Location, DemoHomeSeed.Rooms, DemoHomeSeed.Members)
            : null;

    public IReadOnlyList<RoomWithAppliances>? GetRooms(string homeId)
    {
        if (!IsKnownHome(homeId))
        {
            return null;
        }

        lock (_gate)
        {
            return [.. DemoHomeSeed.Rooms.Select(room => new RoomWithAppliances(
                room.Id,
                room.Name,
                [.. _order.Where(id => _entries[id].Appliance.RoomId == room.Id)]))];
        }
    }

    public IReadOnlyList<Appliance>? GetAppliances(string homeId)
    {
        if (!IsKnownHome(homeId))
        {
            return null;
        }

        lock (_gate)
        {
            return [.. _order.Select(id => ApplianceRounding.ToVisible(_entries[id].Appliance))];
        }
    }

    public Appliance? GetAppliance(string applianceId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(applianceId, out var entry) ? ApplianceRounding.ToVisible(entry.Appliance) : null;
        }
    }

    public IReadOnlyList<TelemetryPoint>? GetTelemetry(string applianceId, DateTime? fromUtc, DateTime? toUtc)
    {
        var to = toUtc ?? UtcNow;
        var from = fromUtc ?? to - DefaultTelemetryWindow;
        lock (_gate)
        {
            return _entries.TryGetValue(applianceId, out var entry)
                ? [.. entry.Telemetry.Where(p => p.TsUtc >= from && p.TsUtc <= to)]
                : null;
        }
    }

    /// <summary>Validates and applies a command, as the real HomeWhiz would.</summary>
    public MockCommandOutcome ExecuteCommand(string applianceId, ApplianceCommand command)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(applianceId, out var entry))
            {
                return MockCommandOutcome.NotFound;
            }

            var visible = ApplianceRounding.ToVisible(entry.Appliance);
            var validation = ApplianceCommandValidator.Validate(visible, command);
            if (!validation.IsValid)
            {
                return new MockCommandOutcome(null, validation.Rejection);
            }

            var state = CommandApplier.Apply(entry.Appliance, validation.Command!, UtcNow);
            entry.Appliance = entry.Appliance with { State = state };

            // The caller publishes this change; the simulator should not announce it again.
            entry.LastVisibleState = state;
            var result = new CommandResult(Guid.NewGuid().ToString(), true, ApplianceRounding.ToVisible(entry.Appliance));
            return new MockCommandOutcome(result, null);
        }
    }

    /// <summary>Advances every appliance to the current simulated time and returns visible changes and events.</summary>
    public SimulationTickResult Tick()
    {
        lock (_gate)
        {
            var now = UtcNow;
            var elapsed = now - _lastTickUtc;
            if (elapsed <= TimeSpan.Zero)
            {
                return SimulationTickResult.Empty;
            }

            _lastTickUtc = now;
            var changed = new List<Appliance>();
            var events = new List<SimulatedApplianceEvent>();
            foreach (var id in _order)
            {
                var entry = _entries[id];
                var step = ApplianceSimulator.Advance(entry.Appliance, elapsed, _outdoorTempC, now);
                var state = step.State;
                if (ApplianceRounding.VisiblyDiffers(state, entry.LastVisibleState))
                {
                    state = state with { UpdatedAtUtc = now };
                    entry.LastVisibleState = state;
                    changed.Add(ApplianceRounding.ToVisible(entry.Appliance with { State = state }));
                }

                entry.Appliance = entry.Appliance with { State = state };
                events.AddRange(step.Events.Select(e => new SimulatedApplianceEvent(entry.Appliance.HomeId, id, e, now)));
            }

            if (now - _lastSampleUtc >= SampleInterval)
            {
                _lastSampleUtc = now;
                foreach (var entry in _entries.Values)
                {
                    entry.AddSample(ToSample(ApplianceRounding.ToVisible(entry.Appliance), now));
                }
            }

            return new SimulationTickResult(changed, events);
        }
    }

    private static bool IsKnownHome(string homeId) => string.Equals(homeId, DemoHomeSeed.HomeId, StringComparison.Ordinal);

    private static TelemetryPoint ToSample(Appliance appliance, DateTime tsUtc) =>
        new(tsUtc, appliance.State.Ac?.RoomTempC, appliance.State.Ac?.HumidityPct, appliance.State.PowerW);

    /// <summary>A deterministic 24 h history so charts are not empty right after start (gentle daily wave around the current values).</summary>
    private static Queue<TelemetryPoint> BackfillTelemetry(Appliance appliance, DateTime nowUtc)
    {
        var samples = new Queue<TelemetryPoint>(TelemetryCapacity);
        var count = (int)(DefaultTelemetryWindow / SampleInterval);
        for (var i = count; i > 0; i--)
        {
            var ts = nowUtc - (SampleInterval * i);
            var wave = Math.Sin(2 * Math.PI * ts.TimeOfDay.TotalHours / 24);
            var state = appliance.State;
            double? room = state.Ac is { } ac ? Math.Round(ac.RoomTempC + (0.8 * wave), 1) : null;
            double? humidity = state.Ac is { } acHumidity ? Math.Round(acHumidity.HumidityPct - (3 * wave)) : null;
            var power = appliance.Type == ApplianceTypes.Refrigerator ? ApplianceSimulator.FridgePowerW : state.Ac is not null && state.Power ? state.PowerW : 0;
            samples.Enqueue(new TelemetryPoint(ts, room, humidity, power));
        }

        samples.Enqueue(ToSample(ApplianceRounding.ToVisible(appliance), nowUtc));
        return samples;
    }

    private sealed class Entry(Appliance appliance, Queue<TelemetryPoint> telemetry)
    {
        public Appliance Appliance { get; set; } = appliance;

        public ApplianceState LastVisibleState { get; set; } = appliance.State;

        public Queue<TelemetryPoint> Telemetry { get; } = telemetry;

        public void AddSample(TelemetryPoint sample)
        {
            while (Telemetry.Count >= TelemetryCapacity)
            {
                Telemetry.Dequeue();
            }

            Telemetry.Enqueue(sample);
        }
    }
}

/// <summary>Outcome of a mock command: accepted (<see cref="Result"/>), rejected (<see cref="Rejection"/>) or not found (both null).</summary>
public sealed record MockCommandOutcome(CommandResult? Result, CommandRejection? Rejection)
{
    public static readonly MockCommandOutcome NotFound = new(null, null);
}

public sealed record SimulatedApplianceEvent(string HomeId, string ApplianceId, string Event, DateTime AtUtc);

public sealed record SimulationTickResult(IReadOnlyList<Appliance> Changed, IReadOnlyList<SimulatedApplianceEvent> Events)
{
    public static readonly SimulationTickResult Empty = new([], []);
}

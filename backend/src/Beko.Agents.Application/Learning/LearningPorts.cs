using System.Collections.Concurrent;
using Beko.Agents.Application.Homes;

namespace Beko.Agents.Application.Learning;

/// <summary>The parts of a stored <c>device_events</c> document the learner and the dashboard need. <see cref="AtUtc"/> is real time.</summary>
public sealed record DeviceEventView(
    string CommandId,
    string DeviceId,
    string Source,
    string MemberId,
    string CommandType,
    double? ValueNumber,
    bool? ValueBool,
    double? AfterSetpoint,
    string? AfterMode,
    DateTime AtUtc);

/// <summary>Reads <c>device_events</c> (Mongo). Complements the write-only <see cref="IDeviceEventStore"/>.</summary>
public interface IDeviceEventReader
{
    Task<IReadOnlyList<DeviceEventView>> GetAsync(string homeId, DateTime sinceUtc, CancellationToken cancellationToken);
}

/// <summary>One answer to the comfort question (Mongo <c>comfort_feedback</c>). <see cref="Sensation"/> is -1 cold, 0 fine, 1 hot.</summary>
public sealed record ComfortFeedbackRecord(
    string Id,
    string HomeId,
    string MemberId,
    string ApplianceId,
    int Sensation,
    string? Clothing,
    string? CommandId,
    double RoomTempC,
    double HumidityPct,
    double PmvBase,
    double Clo,
    DateTime AtUtc,
    string? TraceId,
    string? SpanId);

public interface IComfortFeedbackStore
{
    Task AddAsync(ComfortFeedbackRecord feedback, CancellationToken cancellationToken);

    Task<IReadOnlyList<ComfortFeedbackRecord>> GetAsync(string homeId, DateTime sinceUtc, CancellationToken cancellationToken);
}

/// <summary>Memory-backed event log for tests and runs without MongoDB: both the write and the read side of <c>device_events</c>.</summary>
public sealed class InMemoryDeviceEventLog : IDeviceEventStore, IDeviceEventReader
{
    private readonly ConcurrentQueue<DeviceEventRecord> _events = new();

    public IReadOnlyCollection<DeviceEventRecord> Records => [.. _events];

    public Task AddAsync(DeviceEventRecord deviceEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deviceEvent);
        _events.Enqueue(deviceEvent);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DeviceEventView>> GetAsync(string homeId, DateTime sinceUtc, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeviceEventView>>(
        [
            .. _events
                .Where(e => e.HomeId == homeId && e.AtUtc >= sinceUtc)
                .Select(e => new DeviceEventView(
                    e.CommandId,
                    e.DeviceId,
                    e.Source,
                    e.MemberId,
                    e.CommandType,
                    e.CommandValue.ValueKind == System.Text.Json.JsonValueKind.Number ? e.CommandValue.GetDouble() : null,
                    e.CommandValue.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False ? e.CommandValue.GetBoolean() : null,
                    e.After.Ac?.Setpoint,
                    e.After.Ac?.Mode,
                    e.AtUtc))
                .OrderBy(e => e.AtUtc),
        ]);
}

public sealed class InMemoryComfortFeedbackStore : IComfortFeedbackStore
{
    private readonly ConcurrentQueue<ComfortFeedbackRecord> _items = new();

    public Task AddAsync(ComfortFeedbackRecord feedback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        _items.Enqueue(feedback);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ComfortFeedbackRecord>> GetAsync(string homeId, DateTime sinceUtc, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ComfortFeedbackRecord>>([.. _items.Where(f => f.HomeId == homeId && f.AtUtc >= sinceUtc).OrderBy(f => f.AtUtc)]);
}

using System.Text.Json;
using Beko.Agents.Domain.Homes;

namespace Beko.Agents.Application.Homes;

/// <summary>Stores accepted device commands (Mongo <c>device_events</c>).</summary>
public interface IDeviceEventStore
{
    Task AddAsync(DeviceEventRecord deviceEvent, CancellationToken cancellationToken);
}

/// <summary>One accepted command with the appliance state before and after it.</summary>
public sealed record DeviceEventRecord(
    string CommandId,
    string HomeId,
    string DeviceId,
    string Source,
    string MemberId,
    string CommandType,
    JsonElement CommandValue,
    ApplianceState Before,
    ApplianceState After,
    DateTime AtUtc,
    string? TraceId,
    string? SpanId);

/// <summary>Pushes appliance changes to connected clients (SignalR <c>DeviceHub</c>).</summary>
public interface IDeviceNotifier
{
    Task ApplianceChangedAsync(Appliance appliance, CancellationToken cancellationToken);

    Task ApplianceEventAsync(string homeId, string applianceId, string eventName, DateTime atUtc, CancellationToken cancellationToken);
}

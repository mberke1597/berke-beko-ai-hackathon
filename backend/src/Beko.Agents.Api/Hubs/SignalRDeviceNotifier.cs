using Beko.Agents.Application.Homes;
using Beko.Agents.Domain.Homes;
using Microsoft.AspNetCore.SignalR;

namespace Beko.Agents.Api.Hubs;

/// <summary>Sends appliance changes and events to the home's SignalR group.</summary>
internal sealed class SignalRDeviceNotifier(IHubContext<DeviceHub> hub) : IDeviceNotifier
{
    public Task ApplianceChangedAsync(Appliance appliance, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(appliance);
        return hub.Clients.Group(DeviceHub.HomeGroup(appliance.HomeId))
            .SendAsync(DeviceHub.DeviceStateChanged, new DeviceStateChangedMessage(appliance), cancellationToken);
    }

    public Task ApplianceEventAsync(string homeId, string applianceId, string eventName, DateTime atUtc, CancellationToken cancellationToken) =>
        hub.Clients.Group(DeviceHub.HomeGroup(homeId))
            .SendAsync(DeviceHub.DeviceEvent, new DeviceEventMessage(applianceId, eventName, atUtc), cancellationToken);
}

public sealed record DeviceStateChangedMessage(Appliance Appliance);

public sealed record DeviceEventMessage(string ApplianceId, string Event, DateTime AtUtc);

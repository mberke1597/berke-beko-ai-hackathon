using Microsoft.AspNetCore.SignalR;

namespace Beko.Agents.Api.Hubs;

/// <summary>Live appliance updates at <c>/hubs/devices</c>. Clients join one group per home.</summary>
public sealed class DeviceHub : Hub
{
    public const string Path = "/hubs/devices";

    public const string DeviceStateChanged = "DeviceStateChanged";

    public const string DeviceEvent = "DeviceEvent";

    public static string HomeGroup(string homeId) => $"home:{homeId}";

    /// <summary>Subscribes the caller to <c>DeviceStateChanged</c> and <c>DeviceEvent</c> of the home.</summary>
    public Task JoinHome(string homeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeId);
        return Groups.AddToGroupAsync(Context.ConnectionId, HomeGroup(homeId), Context.ConnectionAborted);
    }
}

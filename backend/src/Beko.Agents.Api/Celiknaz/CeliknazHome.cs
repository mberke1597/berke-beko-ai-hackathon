using Beko.Agents.Api.Hubs;
using Beko.Agents.Application.Common;
using Beko.Agents.Application.Notifications;
using Beko.Agents.Application.Tasks;
using Beko.Agents.Infrastructure.Celiknaz;
using Microsoft.AspNetCore.SignalR;

namespace Beko.Agents.Api.Celiknaz;

public static class CeliknazHomeExtensions
{
    /// <summary>Registers the Çeliknaz slice 2 home services (comfort, energy, presence, tasks, learning, notifications, dashboard).</summary>
    public static IServiceCollection AddCeliknazHome(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddCeliknazHomeServices(configuration);
        services.AddSingleton<IHomeEventPublisher, SignalRHomeEventPublisher>();
        return services;
    }
}

public sealed record ComfortCheckRequestedMessage(string ApplianceId, string MemberId, string? CommandId);

public sealed record NotificationCreatedMessage(Notification Notification);

public sealed record TaskAssignedMessage(HouseholdTask Task);

/// <summary>Sends slice 2 events to the home's <c>DeviceHub</c> group.</summary>
internal sealed class SignalRHomeEventPublisher(IHubContext<DeviceHub> hub) : IHomeEventPublisher
{
    public const string ComfortCheckRequested = "ComfortCheckRequested";

    public const string NotificationCreated = "NotificationCreated";

    public const string TaskAssigned = "TaskAssigned";

    public Task ComfortCheckRequestedAsync(string homeId, string applianceId, string memberId, string? commandId, CancellationToken cancellationToken) =>
        hub.Clients.Group(DeviceHub.HomeGroup(homeId))
            .SendAsync(ComfortCheckRequested, new ComfortCheckRequestedMessage(applianceId, memberId, commandId), cancellationToken);

    public Task NotificationCreatedAsync(string homeId, Notification notification, CancellationToken cancellationToken) =>
        hub.Clients.Group(DeviceHub.HomeGroup(homeId))
            .SendAsync(NotificationCreated, new NotificationCreatedMessage(notification), cancellationToken);

    public Task TaskAssignedAsync(string homeId, HouseholdTask task, CancellationToken cancellationToken) =>
        hub.Clients.Group(DeviceHub.HomeGroup(homeId))
            .SendAsync(TaskAssigned, new TaskAssignedMessage(task), cancellationToken);
}

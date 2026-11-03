using Beko.Agents.Application.Homes;
using Beko.Agents.Application.Notifications;
using Beko.Agents.Application.Tasks;
using Beko.Agents.Domain.Homes;

namespace Beko.Agents.Application.Common;

/// <summary>Hook point in <see cref="DeviceService"/>: called after a command was accepted and recorded.</summary>
public interface IDeviceCommandObserver
{
    Task OnCommandAsync(DeviceEventRecord deviceEvent, CancellationToken cancellationToken);
}

/// <summary>Hook point in the device simulator: called for every appliance event (<c>finished</c>, <c>timer_done</c>, ...).</summary>
public interface IApplianceEventObserver
{
    Task OnApplianceEventAsync(string homeId, string applianceId, string eventName, DateTime atUtc, CancellationToken cancellationToken);
}

/// <summary>Pushes slice 2 events to the clients of a home (SignalR <c>DeviceHub</c>).</summary>
public interface IHomeEventPublisher
{
    Task ComfortCheckRequestedAsync(string homeId, string applianceId, string memberId, string? commandId, CancellationToken cancellationToken);

    Task NotificationCreatedAsync(string homeId, Notification notification, CancellationToken cancellationToken);

    Task TaskAssignedAsync(string homeId, HouseholdTask task, CancellationToken cancellationToken);
}

/// <summary>Result of an automatic command.</summary>
public sealed record AiCommandOutcome(bool Accepted, string? RejectionCode);

/// <summary>Sends commands (default <c>source: "ai"</c>, schedules use <c>schedule</c>) through the normal command pipeline (<see cref="DeviceService"/> and <see cref="IHomeWhizClient"/>).</summary>
public interface IAiCommandSender
{
    Task<AiCommandOutcome> SendAsync(string applianceId, ApplianceCommand command, string memberId, CancellationToken cancellationToken, string source = "ai");
}

/// <summary>Home, rooms and members as HomeWhiz describes them, cached after the first successful read.</summary>
public sealed class HomeDirectory(IHomeWhizClient homeWhiz)
{
    private HomeDetails? _cached;

    public async Task<HomeDetails> GetAsync(CancellationToken cancellationToken)
    {
        if (_cached is { } cached)
        {
            return cached;
        }

        var home = await homeWhiz.GetHomeAsync(DemoHome.HomeId, cancellationToken)
            ?? throw new InvalidOperationException("The demo home is not available from HomeWhiz.");
        _cached = home;
        return home;
    }

    public async Task<bool> IsMemberAsync(string memberId, CancellationToken cancellationToken) =>
        (await GetAsync(cancellationToken)).Members.Any(m => string.Equals(m.Id, memberId, StringComparison.Ordinal));

    public async Task<IReadOnlyList<string>> MemberIdsAsync(CancellationToken cancellationToken) =>
        [.. (await GetAsync(cancellationToken)).Members.Select(m => m.Id)];
}

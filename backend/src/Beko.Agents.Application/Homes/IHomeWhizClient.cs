using Beko.Agents.Domain.Homes;

namespace Beko.Agents.Application.Homes;

/// <summary>
/// Port to HomeWhiz (mocked today). The app reads and writes homes and appliances only through this interface.
/// Methods return <c>null</c> when HomeWhiz answers 404.
/// </summary>
public interface IHomeWhizClient
{
    Task<IReadOnlyList<HomeSummary>> GetHomesAsync(CancellationToken cancellationToken);

    Task<HomeDetails?> GetHomeAsync(string homeId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Appliance>?> GetAppliancesAsync(string homeId, CancellationToken cancellationToken);

    Task<Appliance?> GetApplianceAsync(string applianceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<TelemetryPoint>?> GetTelemetryAsync(string applianceId, DateTime? fromUtc, DateTime? toUtc, CancellationToken cancellationToken);

    /// <summary>Sends a command. Throws <see cref="ApplianceCommandRejectedException"/> when HomeWhiz answers 422.</summary>
    Task<CommandResult?> SendCommandAsync(string applianceId, ApplianceCommand command, CancellationToken cancellationToken);
}

public sealed record HomeSummary(string Id, string Name, string City);

public sealed record HomeDetails(string Id, string Name, string City, GeoLocation Location, IReadOnlyList<Room> Rooms, IReadOnlyList<HouseholdMember> Members);

public sealed record RoomWithAppliances(string Id, string Name, IReadOnlyList<string> ApplianceIds);

public sealed record CommandResult(string CommandId, bool Accepted, Appliance Appliance);

/// <summary>Raised when a command is not supported by the appliance or its value is out of range (HTTP 422).</summary>
public sealed class ApplianceCommandRejectedException(CommandRejection rejection)
    : Exception(rejection?.Detail)
{
    public CommandRejection Rejection { get; } = rejection ?? throw new ArgumentNullException(nameof(rejection));
}

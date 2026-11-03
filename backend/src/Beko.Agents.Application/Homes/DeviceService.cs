using System.Diagnostics;
using Beko.Agents.Application.Telemetry;
using Beko.Agents.Domain.Homes;
using Microsoft.Extensions.Logging;

namespace Beko.Agents.Application.Homes;

/// <summary>App-side use cases for the home and its appliances. Reads and writes only through <see cref="IHomeWhizClient"/>.</summary>
public sealed partial class DeviceService(
    IHomeWhizClient homeWhiz,
    IDeviceEventStore eventStore,
    IDeviceNotifier notifier,
    TimeProvider timeProvider,
    ILogger<DeviceService> logger)
{
    public async Task<MyHome?> GetMyHomeAsync(CancellationToken cancellationToken)
    {
        var home = await homeWhiz.GetHomeAsync(DemoHome.HomeId, cancellationToken);
        if (home is null)
        {
            return null;
        }

        var appliances = await homeWhiz.GetAppliancesAsync(home.Id, cancellationToken) ?? [];
        return new MyHome(
            new HomeSummary(home.Id, home.Name, home.City),
            home.Rooms,
            [.. home.Members.Select(m => new MyHomeMember(m.Id, m.DisplayName, m.Role, m.Id == DemoHome.DefaultMemberId))],
            appliances);
    }

    public Task<Appliance?> GetDeviceAsync(string deviceId, CancellationToken cancellationToken) =>
        homeWhiz.GetApplianceAsync(deviceId, cancellationToken);

    public Task<IReadOnlyList<TelemetryPoint>?> GetTelemetryAsync(string deviceId, DateTime? fromUtc, DateTime? toUtc, CancellationToken cancellationToken) =>
        homeWhiz.GetTelemetryAsync(deviceId, fromUtc, toUtc, cancellationToken);

    /// <summary>
    /// Validates and sends a command, then records it in <c>device_events</c>, counts it and notifies clients.
    /// Returns <c>null</c> when the device does not exist; throws <see cref="ApplianceCommandRejectedException"/> on 422.
    /// </summary>
    public async Task<CommandResult?> SendCommandAsync(string deviceId, DeviceCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        using var activity = BekoTelemetry.ActivitySource.StartActivity("device.command");
        activity?.SetTag("device.id", deviceId);
        activity?.SetTag("device.command.type", command.Command.Type);
        activity?.SetTag("device.command.source", command.Source);
        activity?.SetTag("home.member.id", command.MemberId);

        try
        {
            var before = await homeWhiz.GetApplianceAsync(deviceId, cancellationToken);
            if (before is null)
            {
                return null;
            }

            activity?.SetTag("home.id", before.HomeId);
            var validation = ApplianceCommandValidator.Validate(before, command.Command);
            if (!validation.IsValid)
            {
                throw new ApplianceCommandRejectedException(validation.Rejection!);
            }

            var result = await homeWhiz.SendCommandAsync(deviceId, command.Command, cancellationToken);
            if (result is null)
            {
                return null;
            }

            activity?.SetTag("device.command.id", result.CommandId);
            DeviceMetrics.Commands.Add(
                1,
                new KeyValuePair<string, object?>("source", command.Source),
                new KeyValuePair<string, object?>("type", command.Command.Type));

            await RecordEventAsync(command, before, result, cancellationToken);
            await notifier.ApplianceChangedAsync(result.Appliance, cancellationToken);
            return result;
        }
        catch (ApplianceCommandRejectedException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Rejection.Code);
            activity?.SetTag("device.command.rejection", ex.Rejection.Code);
            throw;
        }
    }

    private async Task RecordEventAsync(DeviceCommand command, Appliance before, CommandResult result, CancellationToken cancellationToken)
    {
        var current = Activity.Current;
        var deviceEvent = new DeviceEventRecord(
            result.CommandId,
            before.HomeId,
            before.Id,
            command.Source,
            command.MemberId,
            command.Command.Type,
            command.Command.Value,
            before.State,
            result.Appliance.State,
            timeProvider.GetUtcNow().UtcDateTime,
            current?.TraceId.ToHexString(),
            current?.SpanId.ToHexString());

        try
        {
            await eventStore.AddAsync(deviceEvent, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The command already reached the appliance; a failed audit write must not turn it into an error.
            current?.AddException(ex);
            LogEventWriteFailed(logger, ex, result.CommandId, before.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not write device event for command {CommandId} on {DeviceId}")]
    private static partial void LogEventWriteFailed(ILogger logger, Exception exception, string commandId, string deviceId);
}

/// <summary>A command from the app with its origin. <see cref="Source"/> is <c>app|remote|voice|ai|schedule</c>.</summary>
public sealed record DeviceCommand(ApplianceCommand Command, string Source, string MemberId);

public sealed record MyHome(HomeSummary Home, IReadOnlyList<Room> Rooms, IReadOnlyList<MyHomeMember> Members, IReadOnlyList<Appliance> Appliances);

public sealed record MyHomeMember(string Id, string DisplayName, string Role, bool IsDefault);

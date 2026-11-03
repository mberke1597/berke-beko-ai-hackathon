using System.Diagnostics;
using System.Text.Json;
using Beko.Agents.Application.Homes;
using Beko.Agents.Application.Telemetry;
using Beko.Agents.Domain.Homes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beko.Agents.Application.Tests.Homes;

public sealed class DeviceServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task SendCommand_Accepted_RecordsEventWithTraceAndNotifies()
    {
        using var listener = ListenToBekoSource();
        var client = new FakeHomeWhizClient();
        var events = new FakeEventStore();
        var notifier = new FakeNotifier();
        var service = CreateService(client, events, notifier);

        var result = await service.SendCommandAsync("ac-salon", Command("setpoint", "22", "voice"), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(22, result.Appliance.State.Ac!.Setpoint);
        var recorded = Assert.Single(events.Events);
        Assert.Equal("home-1", recorded.HomeId);
        Assert.Equal("ac-salon", recorded.DeviceId);
        Assert.Equal("voice", recorded.Source);
        Assert.Equal("ayse", recorded.MemberId);
        Assert.Equal(25, recorded.Before.Ac!.Setpoint);
        Assert.Equal(22, recorded.After.Ac!.Setpoint);
        Assert.False(string.IsNullOrEmpty(recorded.TraceId));
        Assert.False(string.IsNullOrEmpty(recorded.SpanId));
        Assert.Single(notifier.Changed);
    }

    [Fact]
    public async Task SendCommand_OutOfRange_ThrowsWithoutCallingHomeWhiz()
    {
        var client = new FakeHomeWhizClient();
        var events = new FakeEventStore();
        var service = CreateService(client, events, new FakeNotifier());

        var ex = await Assert.ThrowsAsync<ApplianceCommandRejectedException>(() =>
            service.SendCommandAsync("ac-salon", Command("setpoint", "40", "app"), TestContext.Current.CancellationToken));

        Assert.Equal(CommandRejectionCodes.OutOfRange, ex.Rejection.Code);
        Assert.Equal(0, client.CommandsSent);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task SendCommand_UnknownDevice_ReturnsNull()
    {
        var service = CreateService(new FakeHomeWhizClient(), new FakeEventStore(), new FakeNotifier());

        Assert.Null(await service.SendCommandAsync("nope", Command("power", "true", "app"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SendCommand_EventStoreFails_CommandStillSucceeds()
    {
        var service = CreateService(new FakeHomeWhizClient(), new FakeEventStore { Fail = true }, new FakeNotifier());

        var result = await service.SendCommandAsync("ac-salon", Command("power", "false", "app"), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(result.Appliance.State.Power);
    }

    [Fact]
    public async Task GetMyHome_MarksDefaultMember()
    {
        var service = CreateService(new FakeHomeWhizClient(), new FakeEventStore(), new FakeNotifier());

        var home = await service.GetMyHomeAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(home);
        Assert.Equal("home-1", home.Home.Id);
        Assert.True(Assert.Single(home.Members, m => m.IsDefault).Id == "ayse");
        Assert.Single(home.Appliances);
    }

    private static DeviceService CreateService(FakeHomeWhizClient client, FakeEventStore events, FakeNotifier notifier) =>
        new(client, events, notifier, TimeProvider.System, NullLogger<DeviceService>.Instance);

    private static DeviceCommand Command(string type, string valueJson, string source) =>
        new(ApplianceCommand.Create(type, JsonDocument.Parse(valueJson).RootElement), source, DemoHome.DefaultMemberId);

    private static ActivityListener ListenToBekoSource()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BekoTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static Appliance Ac() => new(
        "ac-salon",
        "home-1",
        "salon",
        ApplianceTypes.AirConditioner,
        "Salon Kliması",
        "demo",
        new ApplianceCapabilities(["power", "setpoint", "mode", "fan"], ["cool"], ["auto"], new SetpointRange(16, 30, 0.5), []),
        new ApplianceState(true, ApplianceStatuses.Running, 500, Now, new AcState(25, "cool", "auto", 24.8, 55), null, null, null));

    private sealed class FakeHomeWhizClient : IHomeWhizClient
    {
        private Appliance _appliance = Ac();

        public int CommandsSent { get; private set; }

        public Task<IReadOnlyList<HomeSummary>> GetHomesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HomeSummary>>([new HomeSummary("home-1", "Evim", "İstanbul")]);

        public Task<HomeDetails?> GetHomeAsync(string homeId, CancellationToken cancellationToken) =>
            Task.FromResult<HomeDetails?>(homeId == "home-1"
                ? new HomeDetails("home-1", "Evim", "İstanbul", new GeoLocation(41, 29), [new Room("salon", "Salon")], [new HouseholdMember("ayse", "Ayşe", "owner"), new HouseholdMember("mehmet", "Mehmet", "member")])
                : null);

        public Task<IReadOnlyList<Appliance>?> GetAppliancesAsync(string homeId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Appliance>?>([_appliance]);

        public Task<Appliance?> GetApplianceAsync(string applianceId, CancellationToken cancellationToken) =>
            Task.FromResult(applianceId == _appliance.Id ? _appliance : null);

        public Task<IReadOnlyList<TelemetryPoint>?> GetTelemetryAsync(string applianceId, DateTime? fromUtc, DateTime? toUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TelemetryPoint>?>([]);

        public Task<CommandResult?> SendCommandAsync(string applianceId, ApplianceCommand command, CancellationToken cancellationToken)
        {
            CommandsSent++;
            var state = _appliance.State;
            state = command.Type switch
            {
                "setpoint" => state with { Ac = state.Ac! with { Setpoint = command.Value.GetDouble() } },
                "power" => state with { Power = command.Value.GetBoolean() },
                _ => state,
            };
            _appliance = _appliance with { State = state };
            return Task.FromResult<CommandResult?>(new CommandResult("cmd-1", true, _appliance));
        }
    }

    private sealed class FakeEventStore : IDeviceEventStore
    {
        public List<DeviceEventRecord> Events { get; } = [];

        public bool Fail { get; init; }

        public Task AddAsync(DeviceEventRecord deviceEvent, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new InvalidOperationException("Mongo is down.");
            }

            Events.Add(deviceEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeNotifier : IDeviceNotifier
    {
        public List<Appliance> Changed { get; } = [];

        public Task ApplianceChangedAsync(Appliance appliance, CancellationToken cancellationToken)
        {
            Changed.Add(appliance);
            return Task.CompletedTask;
        }

        public Task ApplianceEventAsync(string homeId, string applianceId, string eventName, DateTime atUtc, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}

using System.Collections.Concurrent;
using System.Text.Json;
using Beko.Agents.Application.Common;
using Beko.Agents.Application.Comfort;
using Beko.Agents.Application.Energy;
using Beko.Agents.Application.Homes;
using Beko.Agents.Application.Learning;
using Beko.Agents.Application.Notifications;
using Beko.Agents.Application.Options;
using Beko.Agents.Application.Presence;
using Beko.Agents.Application.Settings;
using Beko.Agents.Application.Tasks;
using Beko.Agents.Domain.Homes;

namespace Beko.Agents.Application.Tests.Celiknaz;

public sealed class FakeClock : IHomeClock
{
    public DateTime UtcNow { get; set; } = new(2026, 11, 3, 15, 0, 0, DateTimeKind.Utc);

    public double TimeScale { get; set; } = 60;

    public TimeSpan UtcOffset => TimeSpan.FromHours(3);

    public void Advance(TimeSpan span) => UtcNow += span;
}

public sealed class FakeTimeProvider(FakeClock clock) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(clock.UtcNow, TimeSpan.Zero);
}

/// <summary>HomeWhiz with the demo members and a few appliances; commands change the state like the mock.</summary>
public sealed class FakeHome : IHomeWhizClient
{
    private static readonly DateTime Now = new(2026, 11, 3, 15, 0, 0, DateTimeKind.Utc);

    private readonly Dictionary<string, Appliance> _appliances = new(StringComparer.Ordinal);

    public FakeHome(double roomTempC = 27, double humidityPct = 55, bool acOn = true)
    {
        var acCaps = new ApplianceCapabilities(["power", "setpoint", "mode", "fan"], ["cool", "heat", "dry", "fan", "auto", "eco"], ["auto", "low", "medium", "high"], new SetpointRange(16, 30, 0.5), []);
        _appliances["ac-salon"] = new Appliance("ac-salon", "home-1", "salon", ApplianceTypes.AirConditioner, "Salon Kliması", "BK-12", acCaps,
            new ApplianceState(acOn, acOn ? "running" : "off", acOn ? 820 : 0, Now, new AcState(25, "cool", "auto", roomTempC, humidityPct), null, null, null));
        var washerCaps = new ApplianceCapabilities(["power", "start_program"], [], [], null, ["cotton40"]);
        _appliances["dishwasher-1"] = new Appliance("dishwasher-1", "home-1", "mutfak", ApplianceTypes.Dishwasher, "Bulaşık Makinesi", "BDF", washerCaps,
            new ApplianceState(true, "idle", 0, Now, null, new ProgramState("eco50", 50, 180, 0, null), null, null));
        _appliances["washer-1"] = new Appliance("washer-1", "home-1", "camasir-odasi", ApplianceTypes.WashingMachine, "Çamaşır Makinesi", "WTV", washerCaps,
            new ApplianceState(true, "idle", 0, Now, null, new ProgramState("cotton40", 40, 120, 0, null), null, null));
    }

    public ConcurrentQueue<(string ApplianceId, ApplianceCommand Command)> Commands { get; } = new();

    public void Set(Appliance appliance) => _appliances[appliance.Id] = appliance;

    public Task<IReadOnlyList<HomeSummary>> GetHomesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<HomeSummary>>([new HomeSummary("home-1", "Evim", "İstanbul")]);

    public Task<HomeDetails?> GetHomeAsync(string homeId, CancellationToken cancellationToken) =>
        Task.FromResult<HomeDetails?>(new HomeDetails(
            "home-1",
            "Evim",
            "İstanbul",
            new GeoLocation(41.0082, 28.9784),
            [new Room("salon", "Salon")],
            [new HouseholdMember("ayse", "Ayşe", "owner"), new HouseholdMember("mehmet", "Mehmet", "member"), new HouseholdMember("zeynep", "Zeynep", "member")]));

    public Task<IReadOnlyList<Appliance>?> GetAppliancesAsync(string homeId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Appliance>?>([.. _appliances.Values]);

    public Task<Appliance?> GetApplianceAsync(string applianceId, CancellationToken cancellationToken) =>
        Task.FromResult(_appliances.GetValueOrDefault(applianceId));

    public Task<IReadOnlyList<TelemetryPoint>?> GetTelemetryAsync(string applianceId, DateTime? fromUtc, DateTime? toUtc, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TelemetryPoint>?>([]);

    public Task<CommandResult?> SendCommandAsync(string applianceId, ApplianceCommand command, CancellationToken cancellationToken)
    {
        Commands.Enqueue((applianceId, command));
        var a = _appliances[applianceId];
        if (a.State.Ac is { } ac)
        {
            a = command.Type switch
            {
                "power" => a with { State = a.State with { Power = command.Value.GetBoolean() } },
                "setpoint" => a with { State = a.State with { Ac = ac with { Setpoint = command.Value.GetDouble() } } },
                "mode" => a with { State = a.State with { Ac = ac with { Mode = command.Value.GetString()! } } },
                _ => a,
            };
            _appliances[applianceId] = a;
        }

        return Task.FromResult<CommandResult?>(new CommandResult(Guid.NewGuid().ToString(), true, a));
    }
}

public sealed record SentCommand(string ApplianceId, string Type, JsonElement Value, string MemberId, string Source);

public sealed class RecordingSender(FakeHome? home = null) : IAiCommandSender
{
    public ConcurrentQueue<SentCommand> Sent { get; } = new();

    public async Task<AiCommandOutcome> SendAsync(string applianceId, ApplianceCommand command, string memberId, CancellationToken cancellationToken, string source = "ai")
    {
        Sent.Enqueue(new SentCommand(applianceId, command.Type, command.Value, memberId, source));
        if (home is not null)
        {
            await home.SendCommandAsync(applianceId, command, cancellationToken);
        }

        return new AiCommandOutcome(true, null);
    }
}

public sealed class RecordingPublisher : IHomeEventPublisher
{
    public ConcurrentQueue<(string ApplianceId, string MemberId)> ComfortChecks { get; } = new();

    public ConcurrentQueue<Notification> Notifications { get; } = new();

    public ConcurrentQueue<HouseholdTask> Tasks { get; } = new();

    public Task ComfortCheckRequestedAsync(string homeId, string applianceId, string memberId, string? commandId, CancellationToken cancellationToken)
    {
        ComfortChecks.Enqueue((applianceId, memberId));
        return Task.CompletedTask;
    }

    public Task NotificationCreatedAsync(string homeId, Notification notification, CancellationToken cancellationToken)
    {
        Notifications.Enqueue(notification);
        return Task.CompletedTask;
    }

    public Task TaskAssignedAsync(string homeId, HouseholdTask task, CancellationToken cancellationToken)
    {
        Tasks.Enqueue(task);
        return Task.CompletedTask;
    }
}

public static class TestOptions
{
    public static EnergyOptions Energy()
    {
        var o = new EnergyOptions { TariffTlPerKwh = 2.7 };
        o.TimeOfUseBands.Add(new TariffBand { Name = "night", From = "22:00", To = "06:00", TlPerKwh = 1.6 });
        o.TimeOfUseBands.Add(new TariffBand { Name = "day", From = "06:00", To = "17:00", TlPerKwh = 2.6 });
        o.TimeOfUseBands.Add(new TariffBand { Name = "peak", From = "17:00", To = "22:00", TlPerKwh = 3.9 });
        o.EstimatedMonthlyKwh["saving"] = 140;
        o.EstimatedMonthlyKwh["balanced"] = 170;
        o.EstimatedMonthlyKwh["comfort"] = 218;
        o.ProgramProfiles["dishwasher.eco50"] = new ProgramEnergyProfile { Kwh = 0.9, DurationMin = 180 };
        return o;
    }
}

/// <summary>The slice 2 services wired with fakes (no HTTP, no Mongo).</summary>
public sealed class TestWorld
{
    public TestWorld(double roomTempC = 27, double humidityPct = 55, bool acOn = true, bool commandsChangeHome = true)
    {
        Home = new FakeHome(roomTempC, humidityPct, acOn);
        Sender = new RecordingSender(commandsChangeHome ? Home : null);
        var energyOptions = TestOptions.Energy();
        Model = new EnergyModel(energyOptions);
        var simulation = Microsoft.Extensions.Options.Options.Create(new SimulationOptions { OutdoorTempC = 31 });
        var comfortOptions = Microsoft.Extensions.Options.Options.Create(new ComfortOptions());
        var presenceOptions = Microsoft.Extensions.Options.Options.Create(new PresenceOptions());
        Time = new FakeTimeProvider(Clock);

        Directory = new HomeDirectory(Home);
        Profiles = new MemberProfileStore();
        Presence = new PresenceStore();
        Settings = new SettingsService(Profiles, Model);
        Notifications = new NotificationService(Settings, Clock, Publisher);
        Context = new HomeContextService(Directory, Presence, Settings, Clock, simulation, comfortOptions);
        Policy = new ComfortPolicy(Model);
        Comfort = new ComfortService(Home, Context, Settings, Profiles, Presence, Directory, Model, Policy, Clock, comfortOptions);
        Learner = new PatternLearner(Events, Feedback, Profiles, Directory, Notifications, Clock, Time);
        Locations = new MockLocationService(Directory, Clock, presenceOptions);
        PresenceService = new PresenceService(Locations, Locations, Presence, Directory, Settings, Clock, presenceOptions, Home, Sender, Comfort, Notifications, Profiles);
        Tasks = new TaskService(Presence, Directory, Clock, Notifications, Publisher, Home);
        Energy = new EnergyTracker(Model, Clock);
        Learning = new LearningService(Profiles, Directory, Notifications, Time);
        FeedbackService = new ComfortFeedbackService(Comfort, Feedback, Profiles, Settings, Directory, Clock, Time);
        Seeder = new HistorySeeder(Events, Feedback, Learner, Profiles, Energy, Model, Clock, Time);
    }

    public FakeClock Clock { get; } = new();

    public FakeTimeProvider Time { get; }

    public FakeHome Home { get; }

    public RecordingSender Sender { get; }

    public RecordingPublisher Publisher { get; } = new();

    public InMemoryDeviceEventLog Events { get; } = new();

    public InMemoryComfortFeedbackStore Feedback { get; } = new();

    public EnergyModel Model { get; }

    public HomeDirectory Directory { get; }

    public MemberProfileStore Profiles { get; }

    public PresenceStore Presence { get; }

    public SettingsService Settings { get; }

    public NotificationService Notifications { get; }

    public HomeContextService Context { get; }

    public ComfortPolicy Policy { get; }

    public ComfortService Comfort { get; }

    public PatternLearner Learner { get; }

    public MockLocationService Locations { get; }

    public PresenceService PresenceService { get; }

    public TaskService Tasks { get; }

    public EnergyTracker Energy { get; }

    public LearningService Learning { get; }

    public ComfortFeedbackService FeedbackService { get; }

    public HistorySeeder Seeder { get; }
}

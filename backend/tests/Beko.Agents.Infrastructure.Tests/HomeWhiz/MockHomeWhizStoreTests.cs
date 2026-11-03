using System.Text.Json;
using Beko.Agents.Application.Options;
using Beko.Agents.Domain.Homes;
using Beko.Agents.Infrastructure.HomeWhiz.Mock;
using Microsoft.Extensions.Options;

namespace Beko.Agents.Infrastructure.Tests.HomeWhiz;

public sealed class MockHomeWhizStoreTests
{
    [Fact]
    public void Seed_MatchesContract()
    {
        var (store, _) = CreateStore();

        var appliances = store.GetAppliances("home-1")!;

        Assert.Equal(["ac-salon", "ac-yatak", "ac-cocuk", "dishwasher-1", "oven-1", "fridge-1", "washer-1"], appliances.Select(a => a.Id));
        var salon = appliances[0];
        Assert.True(salon.State.Power);
        Assert.Equal(new AcState(25, "cool", "auto", 24.8, 55), salon.State.Ac);
        Assert.All(appliances, a => Assert.Equal(1, new object?[] { a.State.Ac, a.State.Program, a.State.Oven, a.State.Fridge }.Count(o => o is not null)));
        Assert.Null(store.GetAppliances("home-2"));
    }

    [Fact]
    public void ExecuteCommand_Valid_AppliesAndReturnsVisibleAppliance()
    {
        var (store, _) = CreateStore();

        var outcome = store.ExecuteCommand("ac-salon", Command("setpoint", "21.5"));

        Assert.NotNull(outcome.Result);
        Assert.True(outcome.Result.Accepted);
        Assert.Equal(21.5, outcome.Result.Appliance.State.Ac!.Setpoint);
        Assert.Equal(21.5, store.GetAppliance("ac-salon")!.State.Ac!.Setpoint);
    }

    [Fact]
    public void ExecuteCommand_Invalid_IsRejectedAndStateUnchanged()
    {
        var (store, _) = CreateStore();

        var outcome = store.ExecuteCommand("ac-salon", Command("setpoint", "35"));

        Assert.Equal(CommandRejectionCodes.OutOfRange, outcome.Rejection!.Code);
        Assert.Equal(25, store.GetAppliance("ac-salon")!.State.Ac!.Setpoint);
        Assert.Same(MockCommandOutcome.NotFound, store.ExecuteCommand("nope", Command("power", "true")));
    }

    [Fact]
    public void Tick_ReportsOnlyVisibleChanges()
    {
        var (store, time) = CreateStore();

        // 10 real seconds = 10 simulated minutes: the salon AC (24.8 °C toward 25 °C) and the washer change visibly.
        time.Advance(TimeSpan.FromSeconds(10));
        var first = store.Tick();
        Assert.Contains(first.Changed, a => a.Id == "ac-salon");
        Assert.Contains(first.Changed, a => a.Id == "washer-1");
        Assert.DoesNotContain(first.Changed, a => a.Id == "fridge-1");

        // No simulated time has passed: nothing to report.
        Assert.Empty(store.Tick().Changed);
    }

    [Fact]
    public void Tick_WasherFinishes_EmitsFinishedEvent()
    {
        var (store, time) = CreateStore();

        time.Advance(TimeSpan.FromMinutes(1)); // 60 simulated minutes > 42 remaining
        var result = store.Tick();

        var finished = Assert.Single(result.Events);
        Assert.Equal(new SimulatedApplianceEvent("home-1", "washer-1", ApplianceEvents.Finished, store.UtcNow), finished);
        Assert.Equal(ApplianceStatuses.Finished, store.GetAppliance("washer-1")!.State.Status);
    }

    [Fact]
    public void Telemetry_HasBackfilledDayAndNewSamples()
    {
        var (store, time) = CreateStore();
        var initial = store.GetTelemetry("ac-salon", null, null)!;
        Assert.InRange(initial.Count, 280, 300);
        Assert.All(initial, p => Assert.NotNull(p.RoomTempC));
        Assert.All(store.GetTelemetry("washer-1", null, null)!, p => Assert.Null(p.RoomTempC));

        time.Advance(TimeSpan.FromSeconds(10)); // 10 simulated minutes
        store.Tick();

        var latest = store.GetTelemetry("ac-salon", null, null)![^1];
        Assert.Equal(store.UtcNow, latest.TsUtc);
    }

    private static ApplianceCommand Command(string type, string valueJson) =>
        ApplianceCommand.Create(type, JsonDocument.Parse(valueJson).RootElement);

    private static (MockHomeWhizStore Store, ManualTimeProvider Time) CreateStore()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));
        var options = Options.Create(new SimulationOptions { TimeScale = 60, TickSeconds = 5, OutdoorTempC = 31 });
        return (new MockHomeWhizStore(new SimulationClock(time, options), options), time);
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}

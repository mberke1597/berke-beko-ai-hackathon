using Beko.Agents.Domain.Homes;
using Beko.Agents.Infrastructure.HomeWhiz.Mock;

namespace Beko.Agents.Infrastructure.Tests.HomeWhiz;

public sealed class ApplianceSimulatorTests
{
    private const double OutdoorTempC = 31;

    private static readonly DateTime Start = new(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Ac_Running_ConvergesToSetpoint()
    {
        var ac = Seed("ac-yatak");
        ac = Apply(ac, new PowerCommand(true));
        ac = Apply(ac, new SetpointCommand(22));

        var (end, _) = Run(ac, TimeSpan.FromHours(2), TimeSpan.FromMinutes(5));

        Assert.InRange(end.State.Ac!.RoomTempC, 21.9, 22.1);
        Assert.Equal(ApplianceStatuses.Running, end.State.Status);
    }

    [Fact]
    public void Ac_Cooling_DrawsMorePowerWithBiggerGap()
    {
        var hot = new AcState(22, "cool", "auto", 28, 55);
        var close = hot with { RoomTempC = 22.5 };

        Assert.True(ApplianceSimulator.AcPowerW(true, hot) > ApplianceSimulator.AcPowerW(true, close));
        Assert.Equal(0, ApplianceSimulator.AcPowerW(false, hot));
    }

    [Fact]
    public void Ac_Off_DriftsTowardOutdoorTemperature()
    {
        var ac = Apply(Seed("ac-salon"), new PowerCommand(false));

        var (end, _) = Run(ac, TimeSpan.FromHours(4), TimeSpan.FromMinutes(5));

        Assert.True(end.State.Ac!.RoomTempC > 28);
        Assert.True(end.State.Ac.RoomTempC < OutdoorTempC);
        Assert.Equal(0, end.State.PowerW);
    }

    [Fact]
    public void Ac_DryMode_LowersHumidity()
    {
        var ac = Apply(Seed("ac-salon"), new ModeCommand("dry"));
        var before = ac.State.Ac!.HumidityPct;

        var (end, _) = Run(ac, TimeSpan.FromHours(1), TimeSpan.FromMinutes(5));

        Assert.True(end.State.Ac!.HumidityPct < before - 10);
    }

    [Fact]
    public void Ac_StepSizeDoesNotChangeTheResult()
    {
        var ac = Apply(Seed("ac-yatak"), new PowerCommand(true));

        var (fine, _) = Run(ac, TimeSpan.FromHours(1), TimeSpan.FromMinutes(1));
        var (coarse, _) = Run(ac, TimeSpan.FromHours(1), TimeSpan.FromMinutes(30));

        Assert.Equal(fine.State.Ac!.RoomTempC, coarse.State.Ac!.RoomTempC, 6);
    }

    [Fact]
    public void Washer_Program_CountsDownAndFinishesOnce()
    {
        var washer = Apply(Seed("washer-1"), new StopCommand());
        washer = Apply(washer, new StartProgramCommand("quick30", 30, null));
        Assert.Equal(ApplianceStatuses.Running, washer.State.Status);

        var (halfway, _) = Run(washer, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5));
        Assert.Equal(15, halfway.State.Program!.RemainingMin, 6);
        Assert.Equal(50, halfway.State.Program.ProgressPct, 6);

        var (end, events) = Run(washer, TimeSpan.FromMinutes(45), TimeSpan.FromMinutes(5));

        Assert.Equal(ApplianceStatuses.Finished, end.State.Status);
        Assert.Equal(0, end.State.Program!.RemainingMin);
        Assert.Equal(100, end.State.Program.ProgressPct);
        Assert.Equal(0, end.State.PowerW);
        Assert.Equal([ApplianceEvents.Finished], events);
    }

    [Fact]
    public void Washer_Paused_DoesNotCountDown()
    {
        var washer = Apply(Seed("washer-1"), new PauseCommand());

        var (end, events) = Run(washer, TimeSpan.FromHours(2), TimeSpan.FromMinutes(5));

        Assert.Equal(ApplianceStatuses.Paused, end.State.Status);
        Assert.Equal(42, end.State.Program!.RemainingMin);
        Assert.Empty(events);
    }

    [Fact]
    public void Dishwasher_DelayedStart_StartsAtStartTime()
    {
        var dishwasher = Apply(Seed("dishwasher-1"), new StartProgramCommand("quick35", 35, Start.AddMinutes(60)));
        Assert.Equal(ApplianceStatuses.Idle, dishwasher.State.Status);

        var (beforeStart, _) = Run(dishwasher, TimeSpan.FromMinutes(55), TimeSpan.FromMinutes(5));
        Assert.Equal(ApplianceStatuses.Idle, beforeStart.State.Status);

        var (afterStart, _) = Run(dishwasher, TimeSpan.FromMinutes(70), TimeSpan.FromMinutes(5));
        Assert.Equal(ApplianceStatuses.Running, afterStart.State.Status);
        Assert.Equal(20, afterStart.State.Program!.RemainingMin, 6); // quick35 lasts 30 min; 10 min since start
    }

    [Fact]
    public void Oven_HeatsUp_EmitsPreheatedThenTimerDone()
    {
        var oven = Apply(Seed("oven-1"), new SetOvenCommand("conventional", 180, 30));

        var (end, events) = Run(oven, TimeSpan.FromMinutes(40), TimeSpan.FromMinutes(1));

        Assert.Equal([ApplianceEvents.Preheated, ApplianceEvents.TimerDone], events);
        Assert.Equal(ApplianceStatuses.Finished, end.State.Status);
        Assert.Equal(0, end.State.PowerW);
    }

    [Fact]
    public void Fridge_IsStatic()
    {
        var fridge = Seed("fridge-1");

        var (end, events) = Run(fridge, TimeSpan.FromHours(3), TimeSpan.FromMinutes(5));

        Assert.Equal(fridge.State, end.State);
        Assert.Empty(events);
    }

    private static Appliance Seed(string id) => DemoHomeSeed.CreateAppliances(Start).Single(a => a.Id == id);

    private static Appliance Apply(Appliance appliance, ValidCommand command) =>
        appliance with { State = CommandApplier.Apply(appliance, command, Start) };

    private static (Appliance End, List<string> Events) Run(Appliance appliance, TimeSpan duration, TimeSpan step)
    {
        var events = new List<string>();
        var now = Start;
        var end = Start + duration;
        while (now < end)
        {
            var next = now + step > end ? end : now + step;
            var result = ApplianceSimulator.Advance(appliance, next - now, OutdoorTempC, next);
            appliance = appliance with { State = result.State };
            events.AddRange(result.Events);
            now = next;
        }

        return (appliance, events);
    }
}

using Beko.Agents.Domain.Homes;

namespace Beko.Agents.Infrastructure.HomeWhiz.Mock;

/// <summary>Result of advancing one appliance: the new precise state and the events it emitted.</summary>
public sealed record SimulationStep(ApplianceState State, IReadOnlyList<string> Events);

/// <summary>
/// Simple, deterministic appliance physics. Pure functions of (state, simulated elapsed time, outdoor temperature),
/// so they can be unit tested without a clock.
/// </summary>
public static class ApplianceSimulator
{
    /// <summary>Indoor resting temperature an idle oven cools down to.</summary>
    public const double KitchenTempC = 22;

    public const double FridgePowerW = 90;

    private static readonly IReadOnlyList<string> NoEvents = [];

    /// <summary>Advances <paramref name="appliance"/> by <paramref name="elapsed"/> of simulated time ending at <paramref name="nowUtc"/>.</summary>
    public static SimulationStep Advance(Appliance appliance, TimeSpan elapsed, double outdoorTempC, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(appliance);

        var minutes = elapsed.TotalMinutes;
        if (minutes <= 0)
        {
            return new SimulationStep(appliance.State, NoEvents);
        }

        return appliance.Type switch
        {
            ApplianceTypes.AirConditioner => new SimulationStep(AdvanceAc(appliance.State, minutes, outdoorTempC), NoEvents),
            ApplianceTypes.WashingMachine or ApplianceTypes.Dishwasher => AdvanceProgram(appliance, minutes, nowUtc),
            ApplianceTypes.Oven => AdvanceOven(appliance.State, minutes),
            _ => new SimulationStep(appliance.State, NoEvents),
        };
    }

    /// <summary>
    /// AC power draw: compressor power grows with the gap it has to close; when there is nothing to do it idles at fan power.
    /// </summary>
    public static double AcPowerW(bool power, AcState ac)
    {
        ArgumentNullException.ThrowIfNull(ac);
        if (!power)
        {
            return 0;
        }

        var gap = ac.RoomTempC - ac.Setpoint;
        return ac.Mode switch
        {
            "fan" => 60,
            "dry" => 350,
            "cool" => CompressorPowerW(gap),
            "heat" => CompressorPowerW(-gap),
            "eco" => 0.7 * CompressorPowerW(Math.Abs(gap)),
            _ => CompressorPowerW(Math.Abs(gap)),
        };
    }

    /// <summary>Exponential approach of <paramref name="value"/> to <paramref name="target"/> with time constant <paramref name="tauMinutes"/>.</summary>
    public static double Approach(double value, double target, double minutes, double tauMinutes) =>
        value + ((target - value) * (1 - Math.Exp(-minutes / tauMinutes)));

    private static double CompressorPowerW(double demandC) => demandC > 0 ? Math.Min(450 + (350 * demandC), 1600) : 120;

    private static ApplianceState AdvanceAc(ApplianceState state, double minutes, double outdoorTempC)
    {
        if (state.Ac is not { } ac)
        {
            return state;
        }

        double room;
        var humidity = ac.HumidityPct;
        if (!state.Power)
        {
            room = Approach(ac.RoomTempC, outdoorTempC, minutes, 240);
            humidity = Approach(humidity, 60, minutes, 240);
        }
        else
        {
            switch (ac.Mode)
            {
                case "fan":
                    room = Approach(ac.RoomTempC, outdoorTempC, minutes, 240);
                    break;
                case "dry":
                    room = Approach(ac.RoomTempC, ac.Setpoint, minutes, 60);
                    humidity = Approach(humidity, 40, minutes, 30);
                    break;
                case "eco":
                    room = Approach(ac.RoomTempC, ac.Setpoint, minutes, 35);
                    humidity = Approach(humidity, 50, minutes, 90);
                    break;
                case "heat":
                    room = Approach(ac.RoomTempC, ac.Setpoint, minutes, 20);
                    break;
                default:
                    room = Approach(ac.RoomTempC, ac.Setpoint, minutes, 20);
                    humidity = Approach(humidity, 50, minutes, 90);
                    break;
            }
        }

        var next = ac with { RoomTempC = room, HumidityPct = humidity };
        return state with { Ac = next, PowerW = AcPowerW(state.Power, next) };
    }

    private static SimulationStep AdvanceProgram(Appliance appliance, double minutes, DateTime nowUtc)
    {
        var state = appliance.State;
        if (!state.Power || state.Program is not { } program)
        {
            return new SimulationStep(state, NoEvents);
        }

        var runMinutes = 0.0;
        if (state.Status == ApplianceStatuses.Running)
        {
            runMinutes = minutes;
        }
        else if (state.Status == ApplianceStatuses.Idle && program.StartAtUtc is { } startAt && nowUtc >= startAt)
        {
            // Delayed start reached: start now and count the time that already passed since the start time.
            runMinutes = Math.Min(minutes, (nowUtc - startAt).TotalMinutes);
            program = program with { StartAtUtc = null };
            state = state with { Status = ApplianceStatuses.Running, Program = program };
        }

        if (runMinutes <= 0)
        {
            return new SimulationStep(state, NoEvents);
        }

        var profile = ProgramCatalog.Find(appliance.Type, program.Program);
        var durationMin = profile?.DurationMin ?? Math.Max(program.RemainingMin, 1);
        var remaining = Math.Max(0, program.RemainingMin - runMinutes);
        if (remaining <= 0)
        {
            var finished = state with
            {
                Status = ApplianceStatuses.Finished,
                PowerW = 0,
                Program = program with { RemainingMin = 0, ProgressPct = 100 },
            };
            return new SimulationStep(finished, [ApplianceEvents.Finished]);
        }

        var running = state with
        {
            PowerW = profile?.AveragePowerW ?? 400,
            Program = program with { RemainingMin = remaining, ProgressPct = Math.Clamp(100 * (1 - (remaining / durationMin)), 0, 100) },
        };
        return new SimulationStep(running, NoEvents);
    }

    private static SimulationStep AdvanceOven(ApplianceState state, double minutes)
    {
        if (state.Oven is not { } oven)
        {
            return new SimulationStep(state, NoEvents);
        }

        if (state.Status != ApplianceStatuses.Running)
        {
            var cooled = oven with { CurrentTempC = Approach(oven.CurrentTempC, KitchenTempC, minutes, 30) };
            return new SimulationStep(state with { Oven = cooled, PowerW = 0 }, NoEvents);
        }

        var events = new List<string>();
        var ratePerMin = oven.Mode switch
        {
            "fan" => 12,
            "grill" => 15,
            _ => 10,
        };

        var current = oven.CurrentTempC < oven.TargetTempC
            ? Math.Min(oven.TargetTempC, oven.CurrentTempC + (ratePerMin * minutes))
            : Approach(oven.CurrentTempC, oven.TargetTempC, minutes, 30);

        if (oven.CurrentTempC < oven.TargetTempC - 1 && current >= oven.TargetTempC - 1)
        {
            events.Add(ApplianceEvents.Preheated);
        }

        var status = ApplianceStatuses.Running;
        var timer = oven.TimerMin;
        if (oven.TimerMin > 0)
        {
            timer = Math.Max(0, oven.TimerMin - minutes);
            if (timer <= 0)
            {
                events.Add(ApplianceEvents.TimerDone);
                status = ApplianceStatuses.Finished;
            }
        }

        var powerW = status != ApplianceStatuses.Running ? 0 : current < oven.TargetTempC - 0.5 ? 2200 : 900;
        var next = state with { Status = status, PowerW = powerW, Oven = oven with { CurrentTempC = current, TimerMin = timer } };
        return new SimulationStep(next, events);
    }
}

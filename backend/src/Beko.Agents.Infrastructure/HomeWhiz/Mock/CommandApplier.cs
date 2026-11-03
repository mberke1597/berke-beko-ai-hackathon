using Beko.Agents.Domain.Homes;

namespace Beko.Agents.Infrastructure.HomeWhiz.Mock;

/// <summary>Applies a validated command to an appliance's state, as the real product would.</summary>
public static class CommandApplier
{
    public static ApplianceState Apply(Appliance appliance, ValidCommand command, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(appliance);
        ArgumentNullException.ThrowIfNull(command);

        var state = appliance.State;
        var next = command switch
        {
            PowerCommand power => ApplyPower(appliance.Type, state, power.On),
            SetpointCommand setpoint when state.Ac is { } ac => state with { Ac = ac with { Setpoint = setpoint.Setpoint } },
            ModeCommand mode when state.Ac is { } ac => state with { Ac = ac with { Mode = mode.Mode } },
            ModeCommand mode when state.Oven is { } oven => state with { Oven = oven with { Mode = mode.Mode } },
            FanCommand fan when state.Ac is { } ac => state with { Ac = ac with { Fan = fan.Fan } },
            StartProgramCommand start => StartProgram(appliance.Type, state, start, nowUtc),
            PauseCommand => state with { Status = ApplianceStatuses.Paused, PowerW = 0 },
            ResumeCommand => state with { Status = ApplianceStatuses.Running },
            StopCommand => Stop(appliance.Type, state),
            SetOvenCommand setOven when state.Oven is { } oven => state with
            {
                Power = true,
                Status = ApplianceStatuses.Running,
                Oven = oven with { Mode = setOven.Mode, TargetTempC = setOven.TargetTempC, TimerMin = setOven.TimerMin },
            },
            _ => state,
        };

        if (next.Ac is { } nextAc)
        {
            next = next with { PowerW = ApplianceSimulator.AcPowerW(next.Power, nextAc) };
        }

        return next with { UpdatedAtUtc = nowUtc };
    }

    private static ApplianceState ApplyPower(string type, ApplianceState state, bool on)
    {
        if (type == ApplianceTypes.AirConditioner)
        {
            return state with { Power = on, Status = on ? ApplianceStatuses.Running : ApplianceStatuses.Off };
        }

        if (!on)
        {
            return state with
            {
                Power = false,
                Status = ApplianceStatuses.Off,
                PowerW = 0,
                Program = ResetProgram(type, state.Program),
                Oven = state.Oven is { } oven ? oven with { TimerMin = 0 } : null,
            };
        }

        return state.Power ? state : state with { Power = true, Status = ApplianceStatuses.Idle };
    }

    private static ApplianceState StartProgram(string type, ApplianceState state, StartProgramCommand start, DateTime nowUtc)
    {
        var profile = ProgramCatalog.Find(type, start.Program);
        var duration = profile?.DurationMin ?? 60;
        var delayed = start.StartAtUtc is { } startAt && startAt > nowUtc;
        return state with
        {
            Power = true,
            Status = delayed ? ApplianceStatuses.Idle : ApplianceStatuses.Running,
            PowerW = delayed ? 0 : profile?.AveragePowerW ?? 400,
            Program = new ProgramState(start.Program, start.TemperatureC, duration, 0, delayed ? start.StartAtUtc : null),
        };
    }

    private static ApplianceState Stop(string type, ApplianceState state) =>
        state with
        {
            Status = state.Power ? ApplianceStatuses.Idle : ApplianceStatuses.Off,
            PowerW = 0,
            Program = ResetProgram(type, state.Program),
            Oven = state.Oven is { } oven ? oven with { TimerMin = 0 } : null,
        };

    /// <summary>
    /// A stopped washer or dishwasher keeps its selected program, ready to start again with the full duration,
    /// so the program sub-object stays non-null as the contract requires.
    /// </summary>
    public static ProgramState? ResetProgram(string type, ProgramState? program)
    {
        if (program is null)
        {
            return null;
        }

        var duration = ProgramCatalog.Find(type, program.Program)?.DurationMin ?? program.RemainingMin;
        return program with { RemainingMin = duration, ProgressPct = 0, StartAtUtc = null };
    }
}

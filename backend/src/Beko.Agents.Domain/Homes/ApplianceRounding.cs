namespace Beko.Agents.Domain.Homes;

/// <summary>
/// Rounds the precise simulated state to what clients see: 0.1 °C, 1 % humidity, 1 minute remaining,
/// 1 % progress and 10 W. Two states that round the same are the same visible state.
/// </summary>
public static class ApplianceRounding
{
    public static Appliance ToVisible(Appliance appliance)
    {
        ArgumentNullException.ThrowIfNull(appliance);
        return appliance with { State = ToVisible(appliance.State) };
    }

    public static ApplianceState ToVisible(ApplianceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state with
        {
            PowerW = Math.Round(state.PowerW / 10, MidpointRounding.AwayFromZero) * 10,
            Ac = state.Ac is { } ac
                ? ac with { RoomTempC = Temperature(ac.RoomTempC), HumidityPct = Math.Round(ac.HumidityPct, MidpointRounding.AwayFromZero) }
                : null,
            Program = state.Program is { } program
                ? program with
                {
                    RemainingMin = Math.Ceiling(Math.Round(program.RemainingMin, 6)),
                    ProgressPct = Math.Floor(Math.Round(program.ProgressPct, 6)),
                }
                : null,
            Oven = state.Oven is { } oven
                ? oven with { CurrentTempC = Temperature(oven.CurrentTempC), TimerMin = Math.Ceiling(Math.Round(oven.TimerMin, 6)) }
                : null,
            Fridge = state.Fridge is { } fridge
                ? fridge with { FridgeTempC = Temperature(fridge.FridgeTempC), FreezerTempC = Temperature(fridge.FreezerTempC) }
                : null,
        };
    }

    /// <summary>True when the visible parts of the two states differ (the timestamp is ignored).</summary>
    public static bool VisiblyDiffers(ApplianceState a, ApplianceState b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return ToVisible(a) with { UpdatedAtUtc = default } != ToVisible(b) with { UpdatedAtUtc = default };
    }

    private static double Temperature(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);
}

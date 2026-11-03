namespace Beko.Agents.Domain.Homes;

/// <summary>A Beko product in a home, as HomeWhiz describes it (shape fixed by the contract).</summary>
public sealed record Appliance(
    string Id,
    string HomeId,
    string RoomId,
    string Type,
    string DisplayName,
    string Model,
    ApplianceCapabilities Capabilities,
    ApplianceState State);

/// <summary>What an appliance accepts: command types, modes, fan levels, programs and the setpoint range.</summary>
/// <remarks>For ovens <see cref="Setpoint"/> is the allowed target temperature range.</remarks>
public sealed record ApplianceCapabilities(
    IReadOnlyList<string> Commands,
    IReadOnlyList<string> Modes,
    IReadOnlyList<string> FanLevels,
    SetpointRange? Setpoint,
    IReadOnlyList<string> Programs);

public sealed record SetpointRange(double Min, double Max, double Step);

/// <summary>Current state. Exactly the sub-object for the appliance's type is non-null.</summary>
public sealed record ApplianceState(
    bool Power,
    string Status,
    double PowerW,
    DateTime UpdatedAtUtc,
    AcState? Ac,
    ProgramState? Program,
    OvenState? Oven,
    FridgeState? Fridge);

public sealed record AcState(double Setpoint, string Mode, string Fan, double RoomTempC, double HumidityPct);

/// <summary>
/// Washer and dishwasher program state: the selected program. When the appliance is idle it is the program that
/// would start, with its full duration as <see cref="RemainingMin"/>.
/// </summary>
public sealed record ProgramState(string Program, int TemperatureC, double RemainingMin, double ProgressPct, DateTime? StartAtUtc);

public sealed record OvenState(string Mode, double TargetTempC, double CurrentTempC, double TimerMin);

public sealed record FridgeState(double FridgeTempC, double FreezerTempC, bool DoorOpen);

public static class ApplianceTypes
{
    public const string AirConditioner = "air_conditioner";
    public const string WashingMachine = "washing_machine";
    public const string Dishwasher = "dishwasher";
    public const string Oven = "oven";
    public const string Refrigerator = "refrigerator";
}

public static class ApplianceStatuses
{
    public const string Off = "off";
    public const string Idle = "idle";
    public const string Running = "running";
    public const string Paused = "paused";
    public const string Finished = "finished";
    public const string Error = "error";
}

/// <summary>Event names sent with <c>DeviceEvent</c>.</summary>
public static class ApplianceEvents
{
    public const string Finished = "finished";
    public const string Preheated = "preheated";
    public const string TimerDone = "timer_done";
    public const string Error = "error";
}

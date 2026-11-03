using System.Text.Json;

namespace Beko.Agents.Domain.Homes;

/// <summary>Raw command as it arrives over the wire: <c>{ "type": "...", "value": ... }</c>.</summary>
public sealed record ApplianceCommand(string Type, JsonElement Value)
{
    private static readonly JsonElement NullElement = JsonDocument.Parse("null").RootElement;

    /// <summary>Creates a command, turning a missing value into JSON <c>null</c> so it can be serialized.</summary>
    public static ApplianceCommand Create(string type, JsonElement value) =>
        new(type, value.ValueKind == JsonValueKind.Undefined ? NullElement : value);
}

public static class CommandTypes
{
    public const string Power = "power";
    public const string Setpoint = "setpoint";
    public const string Mode = "mode";
    public const string Fan = "fan";
    public const string StartProgram = "start_program";
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Stop = "stop";
    public const string SetOven = "set_oven";
}

/// <summary>A command that passed validation, with its value parsed.</summary>
public abstract record ValidCommand;

public sealed record PowerCommand(bool On) : ValidCommand;

public sealed record SetpointCommand(double Setpoint) : ValidCommand;

public sealed record ModeCommand(string Mode) : ValidCommand;

public sealed record FanCommand(string Fan) : ValidCommand;

public sealed record StartProgramCommand(string Program, int TemperatureC, DateTime? StartAtUtc) : ValidCommand;

public sealed record PauseCommand : ValidCommand;

public sealed record ResumeCommand : ValidCommand;

public sealed record StopCommand : ValidCommand;

public sealed record SetOvenCommand(string Mode, double TargetTempC, int TimerMin) : ValidCommand;

/// <summary>Why a command was rejected. <see cref="Code"/> is stable; <see cref="Title"/> is Turkish for the UI.</summary>
public sealed record CommandRejection(string Code, string Title, string Detail);

public static class CommandRejectionCodes
{
    public const string NotSupported = "command_not_supported";
    public const string InvalidValue = "invalid_value";
    public const string OutOfRange = "value_out_of_range";
    public const string InvalidState = "invalid_state";
}

public sealed record CommandValidationResult(ValidCommand? Command, CommandRejection? Rejection)
{
    public bool IsValid => Command is not null;
}

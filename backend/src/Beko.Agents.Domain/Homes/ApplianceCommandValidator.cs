using System.Globalization;
using System.Text.Json;

namespace Beko.Agents.Domain.Homes;

/// <summary>Checks a raw command against the appliance's capabilities and current state.</summary>
public static class ApplianceCommandValidator
{
    public const int MinProgramTemperatureC = 20;
    public const int MaxProgramTemperatureC = 90;
    public const int MaxOvenTimerMin = 600;

    private const string NotSupportedTitle = "Komut desteklenmiyor";
    private const string InvalidValueTitle = "Geçersiz komut değeri";
    private const string OutOfRangeTitle = "Değer izin verilen aralığın dışında";
    private const string InvalidStateTitle = "Komut cihazın şu anki durumunda uygulanamaz";

    public static CommandValidationResult Validate(Appliance appliance, ApplianceCommand command)
    {
        ArgumentNullException.ThrowIfNull(appliance);
        ArgumentNullException.ThrowIfNull(command);

        var capabilities = appliance.Capabilities;
        if (string.IsNullOrEmpty(command.Type) || !capabilities.Commands.Contains(command.Type))
        {
            return Reject(CommandRejectionCodes.NotSupported, NotSupportedTitle, $"'{appliance.Id}' does not support the command '{command.Type}'.");
        }

        var value = command.Value;
        return command.Type switch
        {
            CommandTypes.Power => ValidatePower(value),
            CommandTypes.Setpoint => ValidateSetpoint(capabilities.Setpoint, value),
            CommandTypes.Mode => ValidateChoice(value, capabilities.Modes, "mode", mode => new ModeCommand(mode)),
            CommandTypes.Fan => ValidateChoice(value, capabilities.FanLevels, "fan level", fan => new FanCommand(fan)),
            CommandTypes.StartProgram => ValidateStartProgram(appliance, value),
            CommandTypes.Pause => RequireStatus(appliance, ApplianceStatuses.Running, new PauseCommand()),
            CommandTypes.Resume => RequireStatus(appliance, ApplianceStatuses.Paused, new ResumeCommand()),
            CommandTypes.Stop => Accept(new StopCommand()),
            CommandTypes.SetOven => ValidateSetOven(capabilities, value),
            _ => Reject(CommandRejectionCodes.NotSupported, NotSupportedTitle, $"Unknown command '{command.Type}'."),
        };
    }

    private static CommandValidationResult ValidatePower(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.True => Accept(new PowerCommand(true)),
            JsonValueKind.False => Accept(new PowerCommand(false)),
            _ => InvalidValue("'power' needs true or false."),
        };

    private static CommandValidationResult ValidateSetpoint(SetpointRange? range, JsonElement value)
    {
        if (range is null)
        {
            return Reject(CommandRejectionCodes.NotSupported, NotSupportedTitle, "This appliance has no setpoint.");
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var setpoint))
        {
            return InvalidValue("'setpoint' needs a number.");
        }

        return IsInRange(range, setpoint)
            ? Accept(new SetpointCommand(setpoint))
            : OutOfRange($"Setpoint must be between {Format(range.Min)} and {Format(range.Max)} in steps of {Format(range.Step)}.");
    }

    private static CommandValidationResult ValidateChoice(JsonElement value, IReadOnlyList<string> allowed, string what, Func<string, ValidCommand> create)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return InvalidValue($"The {what} must be a string.");
        }

        var choice = value.GetString()!;
        return allowed.Contains(choice)
            ? Accept(create(choice))
            : OutOfRange($"Unknown {what} '{choice}'. Allowed: {string.Join(", ", allowed)}.");
    }

    private static CommandValidationResult ValidateStartProgram(Appliance appliance, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("program", out var programElement)
            || programElement.ValueKind != JsonValueKind.String)
        {
            return InvalidValue("'start_program' needs { \"program\": \"...\", \"temperatureC\": 40, \"startAtUtc\": null }.");
        }

        var program = programElement.GetString()!;
        var profile = ProgramCatalog.Find(appliance.Type, program);
        if (!appliance.Capabilities.Programs.Contains(program) || profile is null)
        {
            return OutOfRange($"Unknown program '{program}'. Allowed: {string.Join(", ", appliance.Capabilities.Programs)}.");
        }

        var temperatureC = profile.DefaultTemperatureC;
        if (value.TryGetProperty("temperatureC", out var temperatureElement) && temperatureElement.ValueKind != JsonValueKind.Null)
        {
            if (temperatureElement.ValueKind != JsonValueKind.Number || !temperatureElement.TryGetInt32(out temperatureC))
            {
                return InvalidValue("'temperatureC' must be a whole number.");
            }

            if (temperatureC is < MinProgramTemperatureC or > MaxProgramTemperatureC)
            {
                return OutOfRange($"'temperatureC' must be between {MinProgramTemperatureC} and {MaxProgramTemperatureC}.");
            }
        }

        DateTime? startAtUtc = null;
        if (value.TryGetProperty("startAtUtc", out var startElement) && startElement.ValueKind != JsonValueKind.Null)
        {
            if (startElement.ValueKind != JsonValueKind.String || !startElement.TryGetDateTimeOffset(out var startAt))
            {
                return InvalidValue("'startAtUtc' must be an ISO 8601 date-time.");
            }

            startAtUtc = startAt.UtcDateTime;
        }

        if (appliance.State.Status is ApplianceStatuses.Running or ApplianceStatuses.Paused)
        {
            return Reject(CommandRejectionCodes.InvalidState, InvalidStateTitle, "A program is already running; stop it first.");
        }

        return Accept(new StartProgramCommand(program, temperatureC, startAtUtc));
    }

    private static CommandValidationResult ValidateSetOven(ApplianceCapabilities capabilities, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("mode", out var modeElement) || modeElement.ValueKind != JsonValueKind.String
            || !value.TryGetProperty("targetTempC", out var targetElement) || targetElement.ValueKind != JsonValueKind.Number)
        {
            return InvalidValue("'set_oven' needs { \"mode\": \"fan\", \"targetTempC\": 180, \"timerMin\": 30 }.");
        }

        var mode = modeElement.GetString()!;
        if (!capabilities.Modes.Contains(mode))
        {
            return OutOfRange($"Unknown oven mode '{mode}'. Allowed: {string.Join(", ", capabilities.Modes)}.");
        }

        var targetTempC = targetElement.GetDouble();
        if (capabilities.Setpoint is { } range && !IsInRange(range, targetTempC))
        {
            return OutOfRange($"'targetTempC' must be between {Format(range.Min)} and {Format(range.Max)} in steps of {Format(range.Step)}.");
        }

        var timerMin = 0;
        if (value.TryGetProperty("timerMin", out var timerElement) && timerElement.ValueKind != JsonValueKind.Null)
        {
            if (timerElement.ValueKind != JsonValueKind.Number || !timerElement.TryGetInt32(out timerMin))
            {
                return InvalidValue("'timerMin' must be a whole number.");
            }

            if (timerMin is < 0 or > MaxOvenTimerMin)
            {
                return OutOfRange($"'timerMin' must be between 0 and {MaxOvenTimerMin}.");
            }
        }

        return Accept(new SetOvenCommand(mode, targetTempC, timerMin));
    }

    private static CommandValidationResult RequireStatus(Appliance appliance, string requiredStatus, ValidCommand command) =>
        appliance.State.Status == requiredStatus
            ? Accept(command)
            : Reject(CommandRejectionCodes.InvalidState, InvalidStateTitle, $"The appliance must be '{requiredStatus}' but is '{appliance.State.Status}'.");

    private static bool IsInRange(SetpointRange range, double value)
    {
        if (double.IsNaN(value) || value < range.Min || value > range.Max)
        {
            return false;
        }

        if (range.Step <= 0)
        {
            return true;
        }

        var steps = (value - range.Min) / range.Step;
        return Math.Abs(steps - Math.Round(steps)) < 1e-6;
    }

    private static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static CommandValidationResult Accept(ValidCommand command) => new(command, null);

    private static CommandValidationResult InvalidValue(string detail) =>
        Reject(CommandRejectionCodes.InvalidValue, InvalidValueTitle, detail);

    private static CommandValidationResult OutOfRange(string detail) =>
        Reject(CommandRejectionCodes.OutOfRange, OutOfRangeTitle, detail);

    private static CommandValidationResult Reject(string code, string title, string detail) =>
        new(null, new CommandRejection(code, title, detail));
}

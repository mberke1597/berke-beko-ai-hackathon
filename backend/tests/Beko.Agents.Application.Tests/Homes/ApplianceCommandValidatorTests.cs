using System.Text.Json;
using Beko.Agents.Domain.Homes;

namespace Beko.Agents.Application.Tests.Homes;

public sealed class ApplianceCommandValidatorTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Setpoint_WithinRangeAndStep_IsAccepted()
    {
        var result = ApplianceCommandValidator.Validate(Ac(), Command("setpoint", "22.5"));

        Assert.True(result.IsValid);
        Assert.Equal(new SetpointCommand(22.5), result.Command);
    }

    [Theory]
    [InlineData("15.5")]
    [InlineData("30.5")]
    [InlineData("22.3")]
    public void Setpoint_OutOfRangeOrOffStep_IsRejected(string value)
    {
        var result = ApplianceCommandValidator.Validate(Ac(), Command("setpoint", value));

        Assert.False(result.IsValid);
        Assert.Equal(CommandRejectionCodes.OutOfRange, result.Rejection!.Code);
    }

    [Fact]
    public void Setpoint_NotANumber_IsInvalidValue()
    {
        var result = ApplianceCommandValidator.Validate(Ac(), Command("setpoint", "\"warm\""));

        Assert.Equal(CommandRejectionCodes.InvalidValue, result.Rejection!.Code);
    }

    [Fact]
    public void CommandNotInCapabilities_IsNotSupported()
    {
        var result = ApplianceCommandValidator.Validate(Ac(), Command("start_program", "{\"program\":\"eco50\"}"));

        Assert.Equal(CommandRejectionCodes.NotSupported, result.Rejection!.Code);
    }

    [Fact]
    public void Refrigerator_RejectsPower()
    {
        var fridge = Ac() with
        {
            Type = ApplianceTypes.Refrigerator,
            Capabilities = new ApplianceCapabilities([], [], [], null, []),
        };

        var result = ApplianceCommandValidator.Validate(fridge, Command("power", "false"));

        Assert.Equal(CommandRejectionCodes.NotSupported, result.Rejection!.Code);
    }

    [Theory]
    [InlineData("mode", "\"dry\"", true)]
    [InlineData("mode", "\"turbo\"", false)]
    [InlineData("fan", "\"high\"", true)]
    [InlineData("fan", "\"max\"", false)]
    [InlineData("power", "true", true)]
    [InlineData("power", "1", false)]
    public void Choices_AreCheckedAgainstCapabilities(string type, string value, bool valid)
    {
        Assert.Equal(valid, ApplianceCommandValidator.Validate(Ac(), Command(type, value)).IsValid);
    }

    [Fact]
    public void StartProgram_KnownProgram_UsesDefaultTemperature()
    {
        var result = ApplianceCommandValidator.Validate(Washer(ApplianceStatuses.Idle), Command("start_program", "{\"program\":\"cotton60\",\"startAtUtc\":null}"));

        Assert.Equal(new StartProgramCommand("cotton60", 60, null), result.Command);
    }

    [Theory]
    [InlineData("{\"program\":\"eco50\"}", CommandRejectionCodes.OutOfRange)]
    [InlineData("{\"program\":\"cotton40\",\"temperatureC\":95}", CommandRejectionCodes.OutOfRange)]
    [InlineData("{\"temperatureC\":40}", CommandRejectionCodes.InvalidValue)]
    [InlineData("\"cotton40\"", CommandRejectionCodes.InvalidValue)]
    public void StartProgram_BadValue_IsRejected(string value, string code)
    {
        var result = ApplianceCommandValidator.Validate(Washer(ApplianceStatuses.Idle), Command("start_program", value));

        Assert.Equal(code, result.Rejection!.Code);
    }

    [Fact]
    public void StartProgram_WhileRunning_IsInvalidState()
    {
        var result = ApplianceCommandValidator.Validate(Washer(ApplianceStatuses.Running), Command("start_program", "{\"program\":\"quick30\"}"));

        Assert.Equal(CommandRejectionCodes.InvalidState, result.Rejection!.Code);
    }

    [Fact]
    public void PauseAndResume_RequireMatchingStatus()
    {
        Assert.True(ApplianceCommandValidator.Validate(Washer(ApplianceStatuses.Running), Command("pause", "null")).IsValid);
        Assert.False(ApplianceCommandValidator.Validate(Washer(ApplianceStatuses.Idle), Command("pause", "null")).IsValid);
        Assert.True(ApplianceCommandValidator.Validate(Washer(ApplianceStatuses.Paused), Command("resume", "null")).IsValid);
    }

    [Theory]
    [InlineData("{\"mode\":\"fan\",\"targetTempC\":180,\"timerMin\":30}", true)]
    [InlineData("{\"mode\":\"fan\",\"targetTempC\":300,\"timerMin\":30}", false)]
    [InlineData("{\"mode\":\"steam\",\"targetTempC\":180}", false)]
    [InlineData("{\"mode\":\"grill\",\"targetTempC\":180,\"timerMin\":-5}", false)]
    public void SetOven_ChecksModeTemperatureAndTimer(string value, bool valid)
    {
        Assert.Equal(valid, ApplianceCommandValidator.Validate(Oven(), Command("set_oven", value)).IsValid);
    }

    private static ApplianceCommand Command(string type, string valueJson) =>
        ApplianceCommand.Create(type, JsonDocument.Parse(valueJson).RootElement);

    private static Appliance Ac() => new(
        "ac-salon",
        "home-1",
        "salon",
        ApplianceTypes.AirConditioner,
        "Salon Kliması",
        "Beko BK-12 (demo)",
        new ApplianceCapabilities(
            ["power", "setpoint", "mode", "fan"],
            ["cool", "heat", "dry", "fan", "auto", "eco"],
            ["auto", "low", "medium", "high"],
            new SetpointRange(16, 30, 0.5),
            []),
        new ApplianceState(true, ApplianceStatuses.Running, 500, Now, new AcState(25, "cool", "auto", 24.8, 55), null, null, null));

    private static Appliance Washer(string status) => new(
        "washer-1",
        "home-1",
        "camasir-odasi",
        ApplianceTypes.WashingMachine,
        "Çamaşır Makinesi",
        "demo",
        new ApplianceCapabilities(["power", "start_program", "pause", "resume", "stop"], [], [], null, ["cotton40", "cotton60", "quick30"]),
        new ApplianceState(true, status, 0, Now, null, null, null, null));

    private static Appliance Oven() => new(
        "oven-1",
        "home-1",
        "mutfak",
        ApplianceTypes.Oven,
        "Fırın",
        "demo",
        new ApplianceCapabilities(["power", "mode", "set_oven", "pause", "resume", "stop"], ["conventional", "fan", "grill"], [], new SetpointRange(50, 250, 5), []),
        new ApplianceState(false, ApplianceStatuses.Off, 0, Now, null, null, new OvenState("conventional", 180, 22, 0), null));
}

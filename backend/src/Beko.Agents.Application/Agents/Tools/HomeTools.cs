using System.Text.Json;
using Beko.Agents.Application.Agents.Runs;
using Beko.Agents.Application.Homes;
using Beko.Agents.Application.Options;
using Beko.Agents.Domain.Homes;
using Microsoft.Extensions.Options;

namespace Beko.Agents.Application.Agents.Tools;

internal static class ToolJson
{
    public static readonly JsonSerializerOptions Options = JsonSerializerOptions.Web;

    public static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

/// <summary><c>get_home_status</c>: the home, its members, every appliance with its state and the outdoor temperature.</summary>
public sealed class GetHomeStatusTool(DeviceService devices, IOptions<SimulationOptions> simulation) : IAgentTool
{
    public const string ToolName = "get_home_status";

    public const string ToolDescription =
        "Evin güncel durumunu getirir: odalar, ev halkı, tüm Beko cihazları (klima, çamaşır/bulaşık makinesi, fırın, buzdolabı) "
        + "ve durumları (güç, mod, ayar sıcaklığı, oda sıcaklığı, nem, program, kalan süre, güç tüketimi) ile dış sıcaklık. "
        + "Cihaz kimliklerini (applianceId) ve izin verilen komutları (capabilities) öğrenmek için önce bunu çağır.";

    public const string InputSchemaJson = """{"type":"object","properties":{},"required":[]}""";

    private static readonly JsonElement Schema = ToolJson.Schema(InputSchemaJson);

    public string Name => ToolName;

    public string Description => ToolDescription;

    public JsonElement InputSchema => Schema;

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken)
    {
        var home = await devices.GetMyHomeAsync(cancellationToken);
        if (home is null)
        {
            return new ToolExecutionResult(ToolJson.Serialize(new { error = "home_not_found" }), true, Summary: "home not found");
        }

        var status = new
        {
            home = home.Home,
            outdoorTempC = simulation.Value.OutdoorTempC,
            rooms = home.Rooms,
            members = home.Members,
            appliances = home.Appliances,
        };
        return new ToolExecutionResult(ToolJson.Serialize(status), false, Summary: $"{home.Appliances.Count} appliances");
    }
}

/// <summary>
/// <c>control_device</c>: sends one command with <c>source: "ai"</c> through the app's device service, so the same
/// capability checks, <c>device_events</c> and DeviceHub updates apply. Rejections go back to the model as errors.
/// </summary>
public sealed class ControlDeviceTool(DeviceService devices) : IAgentTool
{
    public const string ToolName = "control_device";

    public const string Source = "ai";

    public const string ToolDescription =
        "Bir Beko cihazına tek bir komut gönderir. type: power (true/false), setpoint (sayı, cihazın aralığında), "
        + "mode, fan, start_program ({program, temperatureC, startAtUtc}), pause, resume, stop (value null), "
        + "set_oven ({mode, targetTempC, timerMin}). Yalnızca cihazın capabilities listesindeki komutları kullan. "
        + "Komut reddedilirse hata kodu (command_not_supported, invalid_value, value_out_of_range, invalid_state) döner; "
        + "bunu kullanıcıya Türkçe ve kısaca açıkla.";

    public const string InputSchemaJson = """
        {
          "type": "object",
          "properties": {
            "applianceId": { "type": "string", "description": "Cihaz kimliği, ör. ac-salon" },
            "type": { "type": "string", "enum": ["power", "setpoint", "mode", "fan", "start_program", "pause", "resume", "stop", "set_oven"] },
            "value": { "description": "Komut değeri; pause/resume/stop için null" }
          },
          "required": ["applianceId", "type"]
        }
        """;

    private static readonly JsonElement Schema = ToolJson.Schema(InputSchemaJson);

    public string Name => ToolName;

    public string Description => ToolDescription;

    public JsonElement InputSchema => Schema;

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("applianceId", out var idElement) || idElement.ValueKind != JsonValueKind.String
            || !input.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            return Error(CommandRejectionCodes.InvalidValue, "applianceId and type are required strings.", null);
        }

        var applianceId = idElement.GetString()!;
        var type = typeElement.GetString()!;
        var value = input.TryGetProperty("value", out var valueElement) ? valueElement : default;
        var command = ApplianceCommand.Create(type, value);
        var appliance = await devices.GetDeviceAsync(applianceId, cancellationToken);
        if (appliance is null)
        {
            return Error("appliance_not_found", $"No appliance '{applianceId}'. Call get_home_status for valid ids.", null);
        }

        var action = new AgentAction(applianceId, appliance.DisplayName, type, JsonValues.ToPlain(command.Value), false);
        try
        {
            var result = await devices.SendCommandAsync(applianceId, new DeviceCommand(command, Source, context.MemberId), cancellationToken);
            if (result is null)
            {
                return Error("appliance_not_found", $"No appliance '{applianceId}'.", action);
            }

            var output = new { accepted = result.Accepted, commandId = result.CommandId, appliance = new { result.Appliance.Id, result.Appliance.State } };
            return new ToolExecutionResult(ToolJson.Serialize(output), false, action with { Accepted = result.Accepted }, $"{applianceId} {type} accepted");
        }
        catch (ApplianceCommandRejectedException ex)
        {
            return Error(ex.Rejection.Code, ex.Rejection.Detail, action);
        }
    }

    private static ToolExecutionResult Error(string code, string detail, AgentAction? action) =>
        new(ToolJson.Serialize(new { error = code, detail }), true, action, $"rejected: {code}");
}

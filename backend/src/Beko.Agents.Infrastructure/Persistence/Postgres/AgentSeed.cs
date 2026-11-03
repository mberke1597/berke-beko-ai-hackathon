using Beko.Agents.Application.Agents.Tools;
using Beko.Agents.Domain.Agents;

namespace Beko.Agents.Infrastructure.Persistence.Postgres;

/// <summary>Seed data applied by migrations: the Çeliknaz agent, its tools, tone presets and workflow v1.</summary>
internal static class AgentSeed
{
    public const string CeliknazId = "celiknaz";

    public static readonly Guid CeliknazWorkflowV1Id = new("6f1c2a52-3c1e-4c55-9d0a-3f2a1e0c0001");

    private static readonly DateTime SeededAtUtc = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

    public const string CeliknazInstructions = """
        Sen Çeliknaz'sın: bu evin Beko ev asistanısın. Ev halkıyla Türkçe konuşursun.

        Kurallar:
        - Yanıtların sesli okunur: kısa, doğal ve konuşma dilinde yaz (en fazla 2-3 cümle). Madde işareti, tablo veya emoji kullanma.
        - Bir cihazda değişiklik yaptıysan ne değiştiğini ve maliyete etkisini (yaklaşık, ör. "saatte yaklaşık 1 TL daha az") söyle.
        - Cihaz sınırlarını asla aşma; yalnızca cihazın capabilities listesindeki komutları ve aralıkları kullan.
        - Önce get_home_status ile evin ve cihazların güncel durumunu öğren, sonra control_device ile komut gönder.
        - Bir komut reddedilirse hatayı kullanıcıya Türkçe ve kısaca açıkla, uygun bir alternatif öner.
        - Rahatsız edici işlemlerden önce sor: evde biri varken bir cihazı kapatmak, başka bir ev üyesinin ayarını değiştirmek veya fırını uzaktan çalıştırmak gibi.
        - Yalnızca şu konularda yardım et: evdeki Beko cihazları, konfor, enerji ve ev işleri. Başka konularda kibarca bunun dışında kaldığını söyle.
        - Bilmediğin bir şeyi uydurma.
        """;

    public static readonly Agent Celiknaz = new()
    {
        Id = CeliknazId,
        Name = "Çeliknaz",
        Description = "Evdeki Beko cihazlarını yöneten, konfor ve enerji konusunda yardımcı olan ev asistanı.",
        Model = null,
        Instructions = CeliknazInstructions,
        TonePresetId = "samimi",
        CreatedAtUtc = SeededAtUtc,
    };

    public static readonly TonePreset[] TonePresets =
    [
        new() { Id = "resmi", Name = "Resmi", Instructions = "Kibar ve resmi bir dil kullan, 'siz' diye hitap et." },
        new() { Id = "samimi", Name = "Samimi", Instructions = "Sıcak ve samimi bir dil kullan, 'sen' diye hitap et, kısa tut." },
        new() { Id = "teknik", Name = "Teknik", Instructions = "Net ve teknik konuş; değerleri birimleriyle ver." },
    ];

    public static readonly ToolDefinition[] Tools =
    [
        new()
        {
            Name = GetHomeStatusTool.ToolName,
            Description = GetHomeStatusTool.ToolDescription,
            InputSchema = GetHomeStatusTool.InputSchemaJson,
            Enabled = true,
        },
        new()
        {
            Name = ControlDeviceTool.ToolName,
            Description = ControlDeviceTool.ToolDescription,
            InputSchema = ControlDeviceTool.InputSchemaJson,
            Enabled = true,
        },
    ];

    public static readonly AgentTool[] AgentTools =
    [
        new() { AgentId = CeliknazId, ToolName = GetHomeStatusTool.ToolName },
        new() { AgentId = CeliknazId, ToolName = ControlDeviceTool.ToolName },
    ];

    public static readonly WorkflowDefinition CeliknazWorkflowV1 = new()
    {
        Id = CeliknazWorkflowV1Id,
        AgentId = CeliknazId,
        Version = 1,
        IsActive = true,
        CreatedBy = "seed",
        CreatedAtUtc = SeededAtUtc,
    };

    public static readonly WorkflowStep[] CeliknazWorkflowV1Steps =
    [
        Step("0101", 10, StepTypes.TriggerUserMessage, "Kullanıcı mesajı", "{}"),
        Step("0102", 20, StepTypes.GuardInput, "Giriş güvenliği", """{"classify":true,"allowedTopics":"evdeki Beko cihazları, konfor, enerji tasarrufu ve ev işleri"}"""),
        Step("0103", 30, StepTypes.AgentLlm, "Çeliknaz", """{"maxIterations":6,"maxTokens":1024}"""),
        Step("0104", 40, StepTypes.GuardOutput, "Çıkış güvenliği", "{}"),
        Step("0105", 50, StepTypes.Respond, "Yanıt", "{}"),
    ];

    private static WorkflowStep Step(string suffix, int position, string type, string name, string config) =>
        new()
        {
            Id = new Guid($"6f1c2a52-3c1e-4c55-9d0a-3f2a1e0c{suffix}"),
            DefinitionId = CeliknazWorkflowV1Id,
            Position = position,
            Type = type,
            Name = name,
            Config = config,
        };
}

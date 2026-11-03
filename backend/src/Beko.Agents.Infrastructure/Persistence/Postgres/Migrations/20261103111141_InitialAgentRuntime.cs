using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Beko.Agents.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class InitialAgentRuntime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cost_daily",
                columns: table => new
                {
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    agent_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    calls = table.Column<long>(type: "bigint", nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cache_read_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cache_write_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cost_usd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cost_daily", x => new { x.day, x.agent_id, x.model });
                });

            migrationBuilder.CreateTable(
                name: "tone_presets",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    instructions = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tone_presets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tools",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    input_schema = table.Column<string>(type: "jsonb", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tools", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    instructions = table.Column<string>(type: "text", nullable: false),
                    tone_preset_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agents", x => x.id);
                    table.ForeignKey(
                        name: "fk_agents_tone_presets_tone_preset_id",
                        column: x => x.tone_preset_id,
                        principalTable: "tone_presets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "agent_subagents",
                columns: table => new
                {
                    parent_agent_id = table.Column<string>(type: "character varying(64)", nullable: false),
                    subagent_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_subagents", x => new { x.parent_agent_id, x.subagent_id });
                    table.ForeignKey(
                        name: "fk_agent_subagents_agents_parent_agent_id",
                        column: x => x.parent_agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_agent_subagents_agents_subagent_id",
                        column: x => x.subagent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agent_tools",
                columns: table => new
                {
                    agent_id = table.Column<string>(type: "character varying(64)", nullable: false),
                    tool_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_tools", x => new { x.agent_id, x.tool_name });
                    table.ForeignKey(
                        name: "fk_agent_tools_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_agent_tools_tools_tool_name",
                        column: x => x.tool_name,
                        principalTable: "tools",
                        principalColumn: "name",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workflow_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_definitions", x => x.id);
                    table.ForeignKey(
                        name: "fk_workflow_definitions_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workflow_steps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_step_id = table.Column<Guid>(type: "uuid", nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    config = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_steps", x => x.id);
                    table.ForeignKey(
                        name: "fk_workflow_steps_workflow_definitions_definition_id",
                        column: x => x.definition_id,
                        principalTable: "workflow_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_workflow_steps_workflow_steps_parent_step_id",
                        column: x => x.parent_step_id,
                        principalTable: "workflow_steps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "tone_presets",
                columns: new[] { "id", "instructions", "name" },
                values: new object[,]
                {
                    { "resmi", "Kibar ve resmi bir dil kullan, 'siz' diye hitap et.", "Resmi" },
                    { "samimi", "Sıcak ve samimi bir dil kullan, 'sen' diye hitap et, kısa tut.", "Samimi" },
                    { "teknik", "Net ve teknik konuş; değerleri birimleriyle ver.", "Teknik" }
                });

            migrationBuilder.InsertData(
                table: "tools",
                columns: new[] { "name", "description", "enabled", "input_schema" },
                values: new object[,]
                {
                    { "control_device", "Bir Beko cihazına tek bir komut gönderir. type: power (true/false), setpoint (sayı, cihazın aralığında), mode, fan, start_program ({program, temperatureC, startAtUtc}), pause, resume, stop (value null), set_oven ({mode, targetTempC, timerMin}). Yalnızca cihazın capabilities listesindeki komutları kullan. Komut reddedilirse hata kodu (command_not_supported, invalid_value, value_out_of_range, invalid_state) döner; bunu kullanıcıya Türkçe ve kısaca açıkla.", true, "{\n  \"type\": \"object\",\n  \"properties\": {\n    \"applianceId\": { \"type\": \"string\", \"description\": \"Cihaz kimliği, ör. ac-salon\" },\n    \"type\": { \"type\": \"string\", \"enum\": [\"power\", \"setpoint\", \"mode\", \"fan\", \"start_program\", \"pause\", \"resume\", \"stop\", \"set_oven\"] },\n    \"value\": { \"description\": \"Komut değeri; pause/resume/stop için null\" }\n  },\n  \"required\": [\"applianceId\", \"type\"]\n}" },
                    { "get_home_status", "Evin güncel durumunu getirir: odalar, ev halkı, tüm Beko cihazları (klima, çamaşır/bulaşık makinesi, fırın, buzdolabı) ve durumları (güç, mod, ayar sıcaklığı, oda sıcaklığı, nem, program, kalan süre, güç tüketimi) ile dış sıcaklık. Cihaz kimliklerini (applianceId) ve izin verilen komutları (capabilities) öğrenmek için önce bunu çağır.", true, "{\"type\":\"object\",\"properties\":{},\"required\":[]}" }
                });

            migrationBuilder.InsertData(
                table: "agents",
                columns: new[] { "id", "created_at_utc", "description", "instructions", "model", "name", "tone_preset_id" },
                values: new object[] { "celiknaz", new DateTime(2026, 11, 3, 0, 0, 0, 0, DateTimeKind.Utc), "Evdeki Beko cihazlarını yöneten, konfor ve enerji konusunda yardımcı olan ev asistanı.", "Sen Çeliknaz'sın: bu evin Beko ev asistanısın. Ev halkıyla Türkçe konuşursun.\n\nKurallar:\n- Yanıtların sesli okunur: kısa, doğal ve konuşma dilinde yaz (en fazla 2-3 cümle). Madde işareti, tablo veya emoji kullanma.\n- Bir cihazda değişiklik yaptıysan ne değiştiğini ve maliyete etkisini (yaklaşık, ör. \"saatte yaklaşık 1 TL daha az\") söyle.\n- Cihaz sınırlarını asla aşma; yalnızca cihazın capabilities listesindeki komutları ve aralıkları kullan.\n- Önce get_home_status ile evin ve cihazların güncel durumunu öğren, sonra control_device ile komut gönder.\n- Bir komut reddedilirse hatayı kullanıcıya Türkçe ve kısaca açıkla, uygun bir alternatif öner.\n- Rahatsız edici işlemlerden önce sor: evde biri varken bir cihazı kapatmak, başka bir ev üyesinin ayarını değiştirmek veya fırını uzaktan çalıştırmak gibi.\n- Yalnızca şu konularda yardım et: evdeki Beko cihazları, konfor, enerji ve ev işleri. Başka konularda kibarca bunun dışında kaldığını söyle.\n- Bilmediğin bir şeyi uydurma.", null, "Çeliknaz", "samimi" });

            migrationBuilder.InsertData(
                table: "agent_tools",
                columns: new[] { "agent_id", "tool_name" },
                values: new object[,]
                {
                    { "celiknaz", "control_device" },
                    { "celiknaz", "get_home_status" }
                });

            migrationBuilder.InsertData(
                table: "workflow_definitions",
                columns: new[] { "id", "agent_id", "created_at_utc", "created_by", "is_active", "version" },
                values: new object[] { new Guid("6f1c2a52-3c1e-4c55-9d0a-3f2a1e0c0001"), "celiknaz", new DateTime(2026, 11, 3, 0, 0, 0, 0, DateTimeKind.Utc), "seed", true, 1 });

            migrationBuilder.InsertData(
                table: "workflow_steps",
                columns: new[] { "id", "config", "definition_id", "name", "parent_step_id", "position", "type" },
                values: new object[,]
                {
                    { new Guid("6f1c2a52-3c1e-4c55-9d0a-3f2a1e0c0101"), "{}", new Guid("6f1c2a52-3c1e-4c55-9d0a-3f2a1e0c0001"), "Kullanıcı mesajı", null, 10, "trigger.user_message" },
                    { new Guid("6f1c2a52-3c1e-4c55-9d0a-3f2a1e0c0102"), "{\"classify\":true,\"allowedTopics\":\"evdeki Beko cihazları, konfor, enerji tasarrufu ve ev işleri\"}", new Guid("6f1c2a52-3c1e-4c55-9d0a-3f2a1e0c0001"), "Giriş güvenliği", null, 20, "guard.input" },
                    { new Guid("6f1c2a52-3c1e-4c55-9d0a-3f2a1e0c0103"), "{\"maxIterations\":6,\"maxTokens\":1024}", new Guid("6f1c2a52-3c1e-4c55-9d0a-3f2a1e0c0001"), "Çeliknaz", null, 30, "agent.llm" },
                    { new Guid("6f1c2a52-3c1e-4c55-9d0a-3f2a1e0c0104"), "{}", new Guid("6f1c2a52-3c1e-4c55-9d0a-3f2a1e0c0001"), "Çıkış güvenliği", null, 40, "guard.output" },
                    { new Guid("6f1c2a52-3c1e-4c55-9d0a-3f2a1e0c0105"), "{}", new Guid("6f1c2a52-3c1e-4c55-9d0a-3f2a1e0c0001"), "Yanıt", null, 50, "respond" }
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_subagents_subagent_id",
                table: "agent_subagents",
                column: "subagent_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_tools_tool_name",
                table: "agent_tools",
                column: "tool_name");

            migrationBuilder.CreateIndex(
                name: "ix_agents_tone_preset_id",
                table: "agents",
                column: "tone_preset_id");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_definitions_agent_id",
                table: "workflow_definitions",
                column: "agent_id",
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_definitions_agent_id_version",
                table: "workflow_definitions",
                columns: new[] { "agent_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workflow_steps_definition_id_parent_step_id_position",
                table: "workflow_steps",
                columns: new[] { "definition_id", "parent_step_id", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_steps_parent_step_id",
                table: "workflow_steps",
                column: "parent_step_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_subagents");

            migrationBuilder.DropTable(
                name: "agent_tools");

            migrationBuilder.DropTable(
                name: "cost_daily");

            migrationBuilder.DropTable(
                name: "workflow_steps");

            migrationBuilder.DropTable(
                name: "tools");

            migrationBuilder.DropTable(
                name: "workflow_definitions");

            migrationBuilder.DropTable(
                name: "agents");

            migrationBuilder.DropTable(
                name: "tone_presets");
        }
    }
}

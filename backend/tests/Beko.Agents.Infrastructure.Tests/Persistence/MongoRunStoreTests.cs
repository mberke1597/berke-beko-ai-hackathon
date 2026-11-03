using Beko.Agents.Application.Agents.Runs;
using Beko.Agents.Infrastructure.Persistence.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace Beko.Agents.Infrastructure.Tests.Persistence;

/// <summary>Real MongoDB (Docker): the BSON mapping of the run records round-trips and uses snake_case names.</summary>
public sealed class MongoFixture : IAsyncLifetime
{
    private readonly MongoDbContainer _container = new MongoDbBuilder("mongo:8.0.34").Build();

    public IMongoDatabase Database { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        Database = new MongoClient(_container.GetConnectionString()).GetDatabase("beko_test");
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

public sealed class MongoRunStoreTests(MongoFixture mongo) : IClassFixture<MongoFixture>
{
    private static readonly DateTime Start = new(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Run_IsUpsertedAndReadBack_WithTotalsAndUserText()
    {
        var store = new MongoRunStore(mongo.Database);
        var running = Run("run-1", Start, RunStatuses.Running, new RunTotals(0, 0, 0, 0, 0m, 0));
        var completed = running with { Status = RunStatuses.Completed, DurationMs = 1234, Totals = new RunTotals(2000, 400, 10, 5, 0.008m, 1234) };

        await store.SaveRunAsync(running, TestContext.Current.CancellationToken);
        await store.SaveRunAsync(completed, TestContext.Current.CancellationToken);

        var read = await store.GetRunAsync("run-1", TestContext.Current.CancellationToken);
        Assert.Equal(completed, read);
        Assert.Equal(1, await mongo.Database.GetCollection<BsonDocument>("runs").CountDocumentsAsync(d => d["run_id"] == "run-1", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecentRuns_AreNewestFirstAndLimited()
    {
        var store = new MongoRunStore(mongo.Database);
        foreach (var i in Enumerable.Range(0, 5))
        {
            await store.SaveRunAsync(Run($"recent-{i}", Start.AddMinutes(1000 + i), RunStatuses.Completed, RunTotals.Empty), TestContext.Current.CancellationToken);
        }

        var recent = await store.GetRecentRunsAsync(3, TestContext.Current.CancellationToken);

        Assert.Equal(["recent-4", "recent-3", "recent-2"], recent.Select(r => r.RunId));
    }

    [Fact]
    public async Task Steps_AreReadInExecutionOrder_AndStoredWithTraceAndSpanIds()
    {
        var store = new MongoRunStore(mongo.Database);
        await store.SaveStepAsync(Step("s2", 2, AgentLlmType), TestContext.Current.CancellationToken);
        await store.SaveStepAsync(Step("s1", 1, "trigger.user_message"), TestContext.Current.CancellationToken);
        await store.SaveStepAsync(Step("s2", 2, AgentLlmType) with { Status = StepStatuses.Completed, CostUsd = 0.004m, Tokens = new StepTokens(10, 5) }, TestContext.Current.CancellationToken);

        var steps = await store.GetStepsAsync("run-steps", TestContext.Current.CancellationToken);

        Assert.Equal(["s1", "s2"], steps.Select(s => s.StepId));
        Assert.Equal(StepStatuses.Completed, steps[1].Status);
        Assert.Equal(0.004m, steps[1].CostUsd);
        Assert.Equal(new StepTokens(10, 5), steps[1].Tokens);
        var raw = await mongo.Database.GetCollection<BsonDocument>("run_steps").Find(d => d["step_id"] == "s2").SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("trace-1", raw["trace_id"].AsString);
        Assert.Equal("span-s2", raw["span_id"].AsString);
        Assert.Equal("run-steps", raw["run_id"].AsString);
    }

    [Fact]
    public async Task LlmToolAndGuardrailDocuments_CarryTraceAndSpanIds()
    {
        var store = new MongoRunStore(mongo.Database);
        await store.AddLlmCallAsync(
            new LlmCallRecord("l1", "run-docs", "s1", "conv", "celiknaz", "claude-sonnet-5-5", 1000, 200, 10, 5, 0.004m, "end_turn", 800, null, Start, "trace-1", "span-1"),
            TestContext.Current.CancellationToken);
        await store.AddToolCallAsync(
            new ToolCallRecord("t1", "run-docs", "s2", "conv", "celiknaz", "control_device", "toolu_1", """{"x":1}""", """{"ok":true}""", false, 12, Start, "trace-1", "span-2"),
            TestContext.Current.CancellationToken);
        await store.AddGuardrailDecisionAsync(
            new GuardrailDecisionRecord("g1", "run-docs", "s3", "conv", "celiknaz", "input", "pii", true, "personal data: tckn", "regex", 1, Start, "trace-1", "span-3"),
            TestContext.Current.CancellationToken);

        foreach (var (collection, field, value) in new[] { ("llm_calls", "model", "claude-sonnet-5-5"), ("tool_calls", "tool", "control_device"), ("guardrail_decisions", "verdict", "pii") })
        {
            var doc = await mongo.Database.GetCollection<BsonDocument>(collection).Find(d => d["run_id"] == "run-docs").SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(value, doc[field].AsString);
            Assert.Equal("trace-1", doc["trace_id"].AsString);
            Assert.StartsWith("span-", doc["span_id"].AsString, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Messages_RoundTripWithActionsOfEveryValueType()
    {
        var store = new MongoConversationStore(mongo.Database);
        await store.AddConversationAsync(new ConversationRecord("conv-m", "celiknaz", "demo-user", "ayse", Start, "trace-1", "span-1"), TestContext.Current.CancellationToken);
        var actions = new AgentAction[]
        {
            new("ac-salon", "Salon Kliması", "setpoint", 22L, true),
            new("ac-salon", "Salon Kliması", "setpoint", 22.5, true),
            new("ac-salon", "Salon Kliması", "power", false, true),
            new("ac-salon", "Salon Kliması", "mode", "cool", true),
            new("washer-1", "Çamaşır Makinesi", "start_program", new Dictionary<string, object?> { ["program"] = "eco40", ["temperatureC"] = 40L, ["startAtUtc"] = null }, false),
            new("washer-1", "Çamaşır Makinesi", "pause", null, false),
        };
        await store.AddMessageAsync(
            new MessageRecord("m2", "conv-m", "celiknaz", "demo-user", "assistant", "Tamam.", "voice", "run-m", Start.AddSeconds(2), actions, "trace-1", "span-1"),
            TestContext.Current.CancellationToken);
        await store.AddMessageAsync(
            new MessageRecord("m1", "conv-m", "celiknaz", "demo-user", "user", "Salonu 22 yap", "voice", "run-m", Start.AddSeconds(1), [], "trace-1", "span-0"),
            TestContext.Current.CancellationToken);

        var conversation = await store.GetConversationAsync("conv-m", TestContext.Current.CancellationToken);
        var messages = await store.GetMessagesAsync("conv-m", TestContext.Current.CancellationToken);

        Assert.Equal("ayse", conversation?.MemberId);
        Assert.Equal(["m1", "m2"], messages.Select(m => m.MessageId));
        var read = messages[1].Actions;
        Assert.Equal(6, read.Count);
        Assert.Equal(22L, read[0].Value);
        Assert.Equal(22.5, read[1].Value);
        Assert.Equal(false, read[2].Value);
        Assert.Equal("cool", read[3].Value);
        Assert.False(read[4].Accepted);
        Assert.Equal("eco40", Assert.IsAssignableFrom<IDictionary<string, object>>(read[4].Value)["program"]);
        Assert.Null(read[5].Value);
        Assert.Null(await store.GetConversationAsync("nope", TestContext.Current.CancellationToken));
    }

    private const string AgentLlmType = "agent.llm";

    private static RunRecord Run(string runId, DateTime startedAt, string status, RunTotals totals) =>
        new(runId, "conv-1", "celiknaz", "demo-user", "ayse", 1, status, startedAt, totals.DurationMs, totals, null, "trace-1", "span-run", "Ev soğudu");

    private static StepRecord Step(string stepId, int sequence, string type) =>
        new(stepId, "run-steps", "conv-1", "celiknaz", null, sequence, type, type, StepStatuses.Running, Start, 0, null, 0m, type, null, null, null, "trace-1", $"span-{stepId}");
}

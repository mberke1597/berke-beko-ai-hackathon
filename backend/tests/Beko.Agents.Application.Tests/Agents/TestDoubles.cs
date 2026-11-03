using Beko.Agents.Application.Agents;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Beko.Agents.Application.Agents.Guardrails;
using Beko.Agents.Application.Agents.Llm;
using Beko.Agents.Application.Agents.Runs;
using Beko.Agents.Application.Agents.Tools;
using Beko.Agents.Application.Agents.Workflow;
using Beko.Agents.Application.Homes;
using Beko.Agents.Application.Options;
using Beko.Agents.Application.Telemetry;
using Beko.Agents.Domain.Agents;
using Beko.Agents.Domain.Homes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Beko.Agents.Application.Tests.Agents;

/// <summary>Scripted <see cref="ILlmClient"/>: no network, records every request.</summary>
public sealed class FakeLlmClient(Func<LlmRequest, int, LlmResponse> respond) : ILlmClient
{
    public ConcurrentQueue<LlmRequest> Requests { get; } = new();

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, Func<string, Task>? onTextDelta, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        var response = respond(request, Requests.Count);
        if (onTextDelta is not null && response.Text.Length > 0)
        {
            await onTextDelta(response.Text);
        }

        return response;
    }

    public static readonly LlmUsage DefaultUsage = new(1000, 200, 0, 0);

    public static LlmResponse Text(string text, string model = TestSettings.DefaultModel) =>
        new(model, [new LlmText(text)], StopReasons.EndTurn, DefaultUsage);

    public static LlmResponse ToolUse(string id, string name, string inputJson, string model = TestSettings.DefaultModel) =>
        new(model, [new LlmToolUse(id, name, JsonDocument.Parse(inputJson).RootElement.Clone())], StopReasons.ToolUse, DefaultUsage);

    public static LlmResponse Verdict(string verdict, string reason = "test") =>
        Text($$"""{"verdict":"{{verdict}}","reason":"{{reason}}"}""", TestSettings.FastModel);

    /// <summary>The default script: Haiku says ok, the main model answers with <paramref name="answer"/>.</summary>
    public static Func<LlmRequest, int, LlmResponse> Answering(string answer) =>
        (request, _) => request.Model == TestSettings.FastModel ? Verdict("ok") : Text(answer);
}

public static class TestSettings
{
    public const string DefaultModel = "claude-sonnet-5-5";
    public const string FastModel = "claude-haiku-5-5";

    public static IOptions<AnthropicOptions> Anthropic { get; } =
        Microsoft.Extensions.Options.Options.Create(new AnthropicOptions { DefaultModel = DefaultModel, FastModel = FastModel, MaxTokens = 1024 });

    public static IOptions<PricingOptions> Pricing()
    {
        var pricing = new PricingOptions();
        pricing.Models[DefaultModel] = new ModelPricing { InputPerMTok = 2m, OutputPerMTok = 10m, CacheReadPerMTok = 0.10m, CacheWritePerMTok = 2.5m };
        pricing.Models[FastModel] = new ModelPricing { InputPerMTok = 0.1m, OutputPerMTok = 0.5m, CacheReadPerMTok = 0.01m, CacheWritePerMTok = 0.125m };
        return Microsoft.Extensions.Options.Options.Create(pricing);
    }
}

public sealed class InMemoryConversationStore : IConversationStore
{
    public ConcurrentDictionary<string, ConversationRecord> Conversations { get; } = new();

    public ConcurrentQueue<MessageRecord> Messages { get; } = new();

    public Task AddConversationAsync(ConversationRecord conversation, CancellationToken cancellationToken)
    {
        Conversations[conversation.ConversationId] = conversation;
        return Task.CompletedTask;
    }

    public Task<ConversationRecord?> GetConversationAsync(string conversationId, CancellationToken cancellationToken) =>
        Task.FromResult(Conversations.GetValueOrDefault(conversationId));

    public Task AddMessageAsync(MessageRecord message, CancellationToken cancellationToken)
    {
        Messages.Enqueue(message);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MessageRecord>> GetMessagesAsync(string conversationId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MessageRecord>>([.. Messages.Where(m => m.ConversationId == conversationId).OrderBy(m => m.CreatedAtUtc)]);
}

public sealed class InMemoryRunStore : IRunStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RunRecord> _runs = [];
    private readonly Dictionary<string, StepRecord> _steps = [];

    public ConcurrentQueue<LlmCallRecord> LlmCalls { get; } = new();

    public ConcurrentQueue<ToolCallRecord> ToolCalls { get; } = new();

    public ConcurrentQueue<GuardrailDecisionRecord> Decisions { get; } = new();

    public List<StepRecord> Steps
    {
        get
        {
            lock (_gate)
            {
                return [.. _steps.Values.OrderBy(s => s.Sequence)];
            }
        }
    }

    public Task SaveRunAsync(RunRecord run, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _runs[run.RunId] = run;
        }

        return Task.CompletedTask;
    }

    public Task SaveStepAsync(StepRecord step, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _steps[step.StepId] = step;
        }

        return Task.CompletedTask;
    }

    public Task AddLlmCallAsync(LlmCallRecord call, CancellationToken cancellationToken)
    {
        LlmCalls.Enqueue(call);
        return Task.CompletedTask;
    }

    public Task AddToolCallAsync(ToolCallRecord call, CancellationToken cancellationToken)
    {
        ToolCalls.Enqueue(call);
        return Task.CompletedTask;
    }

    public Task AddGuardrailDecisionAsync(GuardrailDecisionRecord decision, CancellationToken cancellationToken)
    {
        Decisions.Enqueue(decision);
        return Task.CompletedTask;
    }

    public Task<RunRecord?> GetRunAsync(string runId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_runs.GetValueOrDefault(runId));
        }
    }

    public Task<IReadOnlyList<RunRecord>> GetRecentRunsAsync(int limit, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<RunRecord>>([.. _runs.Values.OrderByDescending(r => r.StartedAtUtc).Take(limit)]);
        }
    }

    public Task<IReadOnlyList<StepRecord>> GetStepsAsync(string runId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<StepRecord>>([.. _steps.Values.Where(s => s.RunId == runId).OrderBy(s => s.Sequence)]);
        }
    }
}

/// <summary>Records the RunHub events in order, as the SignalR notifier would send them.</summary>
public sealed class RecordingRunNotifier : IRunNotifier
{
    public ConcurrentQueue<string> Events { get; } = new();

    public ConcurrentQueue<(string Event, StepView Step)> StepEvents { get; } = new();

    public ConcurrentQueue<(string Text, IReadOnlyList<AgentAction> Actions, RunTotals Totals)> Completed { get; } = new();

    public Task RunStartedAsync(string conversationId, string runId, string agentId, string traceId, CancellationToken cancellationToken)
    {
        Events.Enqueue("RunStarted");
        return Task.CompletedTask;
    }

    public Task StepStartedAsync(string conversationId, string runId, StepView step, CancellationToken cancellationToken)
    {
        Events.Enqueue($"StepStarted:{step.Type}");
        StepEvents.Enqueue(("StepStarted", step));
        return Task.CompletedTask;
    }

    public Task StepCompletedAsync(string conversationId, string runId, StepView step, CancellationToken cancellationToken)
    {
        Events.Enqueue($"StepCompleted:{step.Type}");
        StepEvents.Enqueue(("StepCompleted", step));
        return Task.CompletedTask;
    }

    public Task StepFailedAsync(string conversationId, string runId, StepView step, string error, CancellationToken cancellationToken)
    {
        Events.Enqueue($"StepFailed:{step.Type}");
        StepEvents.Enqueue(("StepFailed", step));
        return Task.CompletedTask;
    }

    public Task TextDeltaAsync(string conversationId, string runId, string delta, CancellationToken cancellationToken)
    {
        Events.Enqueue("TextDelta");
        return Task.CompletedTask;
    }

    public Task RunCompletedAsync(string conversationId, string runId, string text, IReadOnlyList<AgentAction> actions, RunTotals totals, CancellationToken cancellationToken)
    {
        Events.Enqueue("RunCompleted");
        Completed.Enqueue((text, actions, totals));
        return Task.CompletedTask;
    }
}

public sealed class FakeCostRollup : ICostRollup
{
    public ConcurrentQueue<(string AgentId, string Model, decimal CostUsd)> Entries { get; } = new();

    public Task AddAsync(DateOnly day, string agentId, string model, LlmUsage usage, decimal costUsd, CancellationToken cancellationToken)
    {
        Entries.Enqueue((agentId, model, costUsd));
        return Task.CompletedTask;
    }
}

public sealed class FakeAgentRepository(Agent agent, WorkflowDefinition workflow) : IAgentRepository
{
    public Task<IReadOnlyList<Agent>> GetAgentsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Agent>>([agent]);

    public Task<Agent?> GetAgentAsync(string agentId, CancellationToken cancellationToken) =>
        Task.FromResult(agentId == agent.Id ? agent : null);

    public Task<WorkflowDefinition?> GetActiveWorkflowAsync(string agentId, CancellationToken cancellationToken) =>
        Task.FromResult(agentId == agent.Id ? workflow : null);

    public Task<TonePreset?> GetTonePresetAsync(string tonePresetId, CancellationToken cancellationToken) =>
        Task.FromResult<TonePreset?>(new TonePreset { Id = tonePresetId, Name = "Samimi", Instructions = "Sıcak konuş." });
}

/// <summary>The Çeliknaz agent and its workflow v1 as seeded in Postgres (same step order and types).</summary>
public static class TestAgents
{
    public static (Agent Agent, WorkflowDefinition Workflow) Celiknaz(bool guards = true, bool classify = true)
    {
        var agent = new Agent { Id = "celiknaz", Name = "Çeliknaz", Instructions = "Sen Çeliknaz'sın.", TonePresetId = "samimi" };
        agent.Tools.Add(new AgentTool { AgentId = "celiknaz", ToolName = GetHomeStatusTool.ToolName });
        agent.Tools.Add(new AgentTool { AgentId = "celiknaz", ToolName = ControlDeviceTool.ToolName });

        var workflow = new WorkflowDefinition { Id = Guid.NewGuid(), AgentId = "celiknaz", Version = 1, IsActive = true };
        var position = 0;
        void Add(string type, string name, string config = "{}") =>
            workflow.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), DefinitionId = workflow.Id, Position = position += 10, Type = type, Name = name, Config = config });

        Add(StepTypes.TriggerUserMessage, "Kullanıcı mesajı");
        if (guards)
        {
            Add(StepTypes.GuardInput, "Giriş güvenliği", $$"""{"classify":{{(classify ? "true" : "false")}}}""");
        }

        Add(StepTypes.AgentLlm, "Çeliknaz", """{"maxIterations":4,"maxTokens":512}""");
        if (guards)
        {
            Add(StepTypes.GuardOutput, "Çıkış güvenliği");
        }

        Add(StepTypes.Respond, "Yanıt");
        return (agent, workflow);
    }
}

/// <summary>Fake HomeWhiz with one air conditioner; commands update it like the mock does.</summary>
public sealed class FakeHomeWhizClient : IHomeWhizClient
{
    private static readonly DateTime Now = new(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);

    private Appliance _ac = new(
        "ac-salon",
        "home-1",
        "salon",
        ApplianceTypes.AirConditioner,
        "Salon Kliması",
        "Beko BK-12 (demo)",
        new ApplianceCapabilities(["power", "setpoint", "mode", "fan"], ["cool", "heat"], ["auto"], new SetpointRange(16, 30, 0.5), []),
        new ApplianceState(true, ApplianceStatuses.Running, 800, Now, new AcState(25, "cool", "auto", 24.8, 55), null, null, null));

    public ConcurrentQueue<ApplianceCommand> Commands { get; } = new();

    public Task<IReadOnlyList<HomeSummary>> GetHomesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<HomeSummary>>([new HomeSummary("home-1", "Evim", "İstanbul")]);

    public Task<HomeDetails?> GetHomeAsync(string homeId, CancellationToken cancellationToken) =>
        Task.FromResult<HomeDetails?>(new HomeDetails("home-1", "Evim", "İstanbul", new GeoLocation(41, 29), [new Room("salon", "Salon")], [new HouseholdMember("ayse", "Ayşe", "owner")]));

    public Task<IReadOnlyList<Appliance>?> GetAppliancesAsync(string homeId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Appliance>?>([_ac]);

    public Task<Appliance?> GetApplianceAsync(string applianceId, CancellationToken cancellationToken) =>
        Task.FromResult(applianceId == _ac.Id ? _ac : null);

    public Task<IReadOnlyList<TelemetryPoint>?> GetTelemetryAsync(string applianceId, DateTime? fromUtc, DateTime? toUtc, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TelemetryPoint>?>([]);

    public Task<CommandResult?> SendCommandAsync(string applianceId, ApplianceCommand command, CancellationToken cancellationToken)
    {
        Commands.Enqueue(command);
        if (command.Type == "setpoint")
        {
            _ac = _ac with { State = _ac.State with { Ac = _ac.State.Ac! with { Setpoint = command.Value.GetDouble() } } };
        }

        return Task.FromResult<CommandResult?>(new CommandResult("cmd-1", true, _ac));
    }
}

public sealed class RecordingDeviceEventStore : IDeviceEventStore
{
    public ConcurrentQueue<DeviceEventRecord> Events { get; } = new();

    public Task AddAsync(DeviceEventRecord deviceEvent, CancellationToken cancellationToken)
    {
        Events.Enqueue(deviceEvent);
        return Task.CompletedTask;
    }
}

public sealed class NullDeviceNotifier : IDeviceNotifier
{
    public Task ApplianceChangedAsync(Appliance appliance, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ApplianceEventAsync(string homeId, string applianceId, string eventName, DateTime atUtc, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>The whole engine wired by hand around fakes.</summary>
public sealed class RunHarness
{
    private RunHarness(FakeLlmClient llm, bool guards, bool classify)
    {
        Llm = llm;
        var (agent, workflow) = TestAgents.Celiknaz(guards, classify);
        var agents = new FakeAgentRepository(agent, workflow);
        var devices = new DeviceService(HomeWhiz, DeviceEvents, new NullDeviceNotifier(), TimeProvider.System, NullLogger<DeviceService>.Instance);
        var tools = new ToolRegistry([new GetHomeStatusTool(devices, Microsoft.Extensions.Options.Options.Create(new SimulationOptions())), new ControlDeviceTool(devices)]);
        var recorder = new RunRecorder(Runs, Notifier, Rollup, llm, new CostCalculator(TestSettings.Pricing()), TimeProvider.System, NullLogger<RunRecorder>.Instance);
        IStepExecutor[] executors =
        [
            new UserMessageTriggerExecutor(Conversations),
            new InputGuardExecutor(TestSettings.Anthropic, TimeProvider.System),
            new AgentLlmExecutor(agents, tools, TestSettings.Anthropic, TimeProvider.System),
            new OutputGuardExecutor(TimeProvider.System),
            new RespondExecutor(Conversations, TimeProvider.System),
        ];
        Engine = new WorkflowEngine(agents, executors, recorder, Conversations, TestSettings.Anthropic, TimeProvider.System, NullLogger<WorkflowEngine>.Instance);
    }

    public FakeLlmClient Llm { get; }

    public InMemoryRunStore Runs { get; } = new();

    public InMemoryConversationStore Conversations { get; } = new();

    public RecordingRunNotifier Notifier { get; } = new();

    public FakeCostRollup Rollup { get; } = new();

    public FakeHomeWhizClient HomeWhiz { get; } = new();

    public RecordingDeviceEventStore DeviceEvents { get; } = new();

    public WorkflowEngine Engine { get; }

    public static RunHarness Create(Func<LlmRequest, int, LlmResponse> script, bool guards = true, bool classify = true) =>
        new(new FakeLlmClient(script), guards, classify);

    /// <summary>Runs one user message through the engine inside a parent activity, like the API request would.</summary>
    public async Task<RunRecord> RunAsync(string text, string conversationId = "conv-1")
    {
        using var activity = new Activity("test.request").Start();
        var userMessage = new MessageRecord(
            Guid.NewGuid().ToString("N"), conversationId, "celiknaz", ChatService.DemoUserId, MessageRoles.User, PiiDetector.Redact(text),
            "text", null, DateTime.UtcNow, [], activity.TraceId.ToHexString(), activity.SpanId.ToHexString());
        await Conversations.AddMessageAsync(userMessage, CancellationToken.None);
        var request = new RunRequest(
            Guid.NewGuid().ToString("N"), conversationId, "celiknaz", ChatService.DemoUserId, "ayse", userMessage.MessageId, text, "text", activity.Context);
        return await Engine.ExecuteAsync(request, CancellationToken.None);
    }
}

/// <summary>Captures spans and metric measurements of the platform's own source and meter.</summary>
public sealed class TelemetryCapture : IDisposable
{
    private readonly ActivityListener _activities;
    private readonly MeterListener _meters;

    public TelemetryCapture()
    {
        _activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BekoTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => Spans.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(_activities);

        _meters = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == BekoTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _meters.SetMeasurementEventCallback<long>((inst, value, tags, _) => Measurements.Enqueue((inst.Name, value, tags.ToArray())));
        _meters.SetMeasurementEventCallback<double>((inst, value, tags, _) => Measurements.Enqueue((inst.Name, value, tags.ToArray())));
        _meters.Start();
    }

    public ConcurrentQueue<Activity> Spans { get; } = new();

    public ConcurrentQueue<(string Instrument, double Value, KeyValuePair<string, object?>[] Tags)> Measurements { get; } = new();

    public IEnumerable<(double Value, KeyValuePair<string, object?>[] Tags)> Of(string instrument) =>
        Measurements.Where(m => m.Instrument == instrument).Select(m => (m.Value, m.Tags));

    public static bool HasTag(KeyValuePair<string, object?>[] tags, string key, string value) =>
        tags.Any(t => t.Key == key && Equals(t.Value, value));

    public void Dispose()
    {
        _activities.Dispose();
        _meters.Dispose();
    }
}

[CollectionDefinition("RunTelemetry", DisableParallelization = true)]
public sealed class RunTelemetryGroup;

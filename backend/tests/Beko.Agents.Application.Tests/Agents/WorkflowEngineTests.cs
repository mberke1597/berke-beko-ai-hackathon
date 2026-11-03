using Beko.Agents.Application.Agents;
using Beko.Agents.Application.Agents.Llm;
using Beko.Agents.Application.Agents.Runs;
using Beko.Agents.Application.Agents.Workflow;
using Beko.Agents.Domain.Agents;

namespace Beko.Agents.Application.Tests.Agents;

[Collection("RunTelemetry")]
public sealed class WorkflowEngineTests
{
    [Fact]
    public async Task Engine_RunsWorkflowStepsInOrder_AndCompletesOnce()
    {
        var harness = RunHarness.Create(FakeLlmClient.Answering("Salon 25 derece."));

        var run = await harness.RunAsync("Merhaba");

        Assert.Equal(RunStatuses.Completed, run.Status);
        Assert.Equal(1, run.WorkflowVersion);
        string[] topLevel =
        [
            StepTypes.TriggerUserMessage, StepTypes.GuardInput, StepTypes.AgentLlm, StepTypes.GuardOutput, StepTypes.Respond,
        ];
        Assert.Equal(topLevel, harness.Runs.Steps.Where(s => s.ParentStepId is null).Select(s => s.Type));

        var events = harness.Notifier.Events.ToList();
        Assert.Equal("RunStarted", events[0]);
        Assert.Equal("RunCompleted", events[^1]);
        Assert.Single(events, e => e == "RunCompleted");
        var started = events.Where(e => e.StartsWith("StepStarted:", StringComparison.Ordinal)).Select(e => e["StepStarted:".Length..]);
        Assert.Equal(topLevel, started.Where(topLevel.Contains));

        var (text, actions, totals) = Assert.Single(harness.Notifier.Completed);
        Assert.Equal("Salon 25 derece.", text);
        Assert.Empty(actions);
        Assert.True(totals.CostUsd > 0);
    }

    [Fact]
    public async Task Engine_StoresAnswerAsAssistantMessage()
    {
        var harness = RunHarness.Create(FakeLlmClient.Answering("Tamam."));

        var run = await harness.RunAsync("Merhaba");

        var assistant = Assert.Single(harness.Conversations.Messages, m => m.Role == MessageRoles.Assistant);
        Assert.Equal("Tamam.", assistant.Text);
        Assert.Equal(run.RunId, assistant.RunId);
        Assert.False(string.IsNullOrEmpty(assistant.TraceId));
    }

    [Fact]
    public async Task Engine_HistoryBecomesEarlierMessagesOfTheModelRequest()
    {
        var harness = RunHarness.Create(FakeLlmClient.Answering("Tamam."), guards: false);
        await harness.RunAsync("Selam");
        await Task.Delay(5, TestContext.Current.CancellationToken);

        await harness.RunAsync("Ev soğudu");

        var secondRequest = harness.Llm.Requests.Last();
        Assert.Equal(["user", "assistant", "user"], secondRequest.Messages.Select(m => m.Role));
    }

    [Fact]
    public async Task Engine_StepTypeWithoutExecutor_FailsRunAndSendsFallback()
    {
        var (_, workflow) = TestAgents.Celiknaz(guards: false);
        workflow.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), DefinitionId = workflow.Id, Position = 15, Type = "knowledge.search", Name = "Bilgi" });

        var run = await new EngineWithWorkflow(workflow).RunAsync();

        Assert.Equal(RunStatuses.Failed, run.Status);
    }

    [Fact]
    public async Task Engine_UnknownAgent_FailsRunAndStillSendsRunCompleted()
    {
        var harness = RunHarness.Create(FakeLlmClient.Answering("x"));
        var request = new RunRequest("run-x", "conv-x", "no-such-agent", ChatService.DemoUserId, "ayse", "m1", "Merhaba", "text", default);

        var run = await harness.Engine.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(RunStatuses.Failed, run.Status);
        var (text, _, _) = Assert.Single(harness.Notifier.Completed);
        Assert.Equal(WorkflowEngine.FallbackAnswer, text);
    }

    /// <summary>Small wrapper that builds an engine around a custom workflow and runs one message.</summary>
    private sealed class EngineWithWorkflow(WorkflowDefinition workflow)
    {
        public async Task<RunRecord> RunAsync()
        {
            var agent = new Agent { Id = "celiknaz", Name = "Çeliknaz" };
            var notifier = new RecordingRunNotifier();
            var runs = new InMemoryRunStore();
            var conversations = new InMemoryConversationStore();
            var agents = new FakeAgentRepository(agent, workflow);
            var llm = new FakeLlmClient(FakeLlmClient.Answering("x"));
            var recorder = new RunRecorder(
                runs, notifier, new FakeCostRollup(), llm, new CostCalculator(TestSettings.Pricing()), TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RunRecorder>.Instance);
            var engine = new WorkflowEngine(
                agents, [new UserMessageTriggerExecutor(conversations)], recorder, conversations, TestSettings.Anthropic, TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkflowEngine>.Instance);
            var request = new RunRequest("run-1", "conv-1", "celiknaz", ChatService.DemoUserId, "ayse", "m1", "Merhaba", "text", default);
            var run = await engine.ExecuteAsync(request, CancellationToken.None);
            Assert.Equal(WorkflowEngine.FallbackAnswer, Assert.Single(notifier.Completed).Text);
            Assert.Contains(notifier.Events, e => e == "StepFailed:knowledge.search");
            return run;
        }
    }
}

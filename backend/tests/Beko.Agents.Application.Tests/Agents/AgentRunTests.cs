using Beko.Agents.Application.Agents;
using Beko.Agents.Application.Agents.Llm;
using Beko.Agents.Application.Agents.Runs;
using Beko.Agents.Application.Agents.Tools;
using Beko.Agents.Application.Agents.Workflow;
using Beko.Agents.Domain.Agents;
using Beko.Agents.Domain.Homes;

using Beko.Agents.TestSupport;

namespace Beko.Agents.Application.Tests.Agents;

[Collection("RunTelemetry")]
public sealed class AgentRunTests
{
    private const string SetpointCall = """{"applianceId":"ac-salon","type":"setpoint","value":22}""";

    [Fact]
    public async Task ToolLoop_ControlDevice_ProducesActionAndDeviceCommand()
    {
        var harness = RunHarness.Create(Script(("t1", "control_device", SetpointCall), "Salonu 22 dereceye aldım."), guards: false);

        var run = await harness.RunAsync("Salonu 22 yap");

        Assert.Equal(RunStatuses.Completed, run.Status);
        var action = Assert.Single(harness.Notifier.Completed.Single().Actions);
        Assert.Equal("ac-salon", action.ApplianceId);
        Assert.Equal("Salon Kliması", action.DisplayName);
        Assert.Equal("setpoint", action.Command);
        Assert.Equal(22L, action.Value);
        Assert.True(action.Accepted);

        var command = Assert.Single(harness.HomeWhiz.Commands);
        Assert.Equal("setpoint", command.Type);
        var deviceEvent = Assert.Single(harness.DeviceEvents.Events);
        Assert.Equal("ai", deviceEvent.Source);
        Assert.Equal("ayse", deviceEvent.MemberId);

        var assistant = Assert.Single(harness.Conversations.Messages, m => m.Role == MessageRoles.Assistant);
        Assert.Equal("ac-salon", Assert.Single(assistant.Actions).ApplianceId);
    }

    [Fact]
    public async Task ToolLoop_SendsToolResultBackToTheModel()
    {
        var harness = RunHarness.Create(Script(("t1", "get_home_status", "{}"), "Salon 24.8 derece."), guards: false);

        await harness.RunAsync("Evin durumu ne?");

        var second = harness.Llm.Requests.ToList()[1];
        var result = Assert.IsType<LlmToolResult>(second.Messages[^1].Content.Single());
        Assert.Equal("t1", result.ToolUseId);
        Assert.False(result.IsError);
        Assert.Contains("ac-salon", result.Content, StringComparison.Ordinal);
        Assert.Contains("outdoorTempC", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolLoop_RejectedCommand_GoesBackAsToolResultError_AndActionIsNotAccepted()
    {
        const string outOfRange = """{"applianceId":"ac-salon","type":"setpoint","value":99}""";
        var harness = RunHarness.Create(Script(("t1", "control_device", outOfRange), "Bu değer cihazın sınırı dışında."), guards: false);

        var run = await harness.RunAsync("Salonu 99 yap");

        Assert.Equal(RunStatuses.Completed, run.Status);
        Assert.Empty(harness.HomeWhiz.Commands);
        var second = harness.Llm.Requests.ToList()[1];
        var result = Assert.IsType<LlmToolResult>(second.Messages[^1].Content.Single());
        Assert.True(result.IsError);
        Assert.Contains(CommandRejectionCodes.OutOfRange, result.Content, StringComparison.Ordinal);
        Assert.False(Assert.Single(harness.Notifier.Completed.Single().Actions).Accepted);
        Assert.True(harness.Runs.ToolCalls.Single().IsError);
    }

    [Fact]
    public async Task ToolLoop_UnknownTool_IsAnErrorResult_NotACrash()
    {
        var harness = RunHarness.Create(Script(("t1", "launch_rocket", "{}"), "Bunu yapamam."), guards: false);

        var run = await harness.RunAsync("Roket fırlat");

        Assert.Equal(RunStatuses.Completed, run.Status);
        var result = Assert.IsType<LlmToolResult>(harness.Llm.Requests.ToList()[1].Messages[^1].Content.Single());
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ToolLoop_IterationCap_StopsWithAnswer()
    {
        // The model keeps asking for tools; the config caps the loop at 4 iterations.
        var harness = RunHarness.Create((_, _) => FakeLlmClient.ToolUse("t", "get_home_status", "{}"), guards: false);

        var run = await harness.RunAsync("Sonsuz döngü");

        Assert.Equal(RunStatuses.Completed, run.Status);
        Assert.Equal(4, harness.Llm.Requests.Count);
        Assert.Equal(AgentLlmExecutor.IterationCapAnswer, harness.Notifier.Completed.Single().Text);
    }

    [Fact]
    public async Task EveryStep_ProducesSpanMongoDocumentAndEvents()
    {
        using var telemetry = new TelemetryCapture();
        var harness = RunHarness.Create(Script(("t1", "control_device", SetpointCall), "Tamam."));

        var run = await harness.RunAsync("Salonu 22 yap");

        var steps = harness.Runs.Steps;
        // trigger, guard.input (+ haiku llm.call), agent.llm (+ 2 llm.call, 1 tool.call), guard.output, respond
        Assert.Equal(9, steps.Count);
        Assert.All(steps, step =>
        {
            Assert.Equal(StepStatuses.Completed, step.Status);
            Assert.Equal(run.TraceId, step.TraceId);
            Assert.False(string.IsNullOrEmpty(step.SpanId));
        });
        Assert.Equal(steps.Count, steps.Select(s => s.SpanId).Distinct().Count());

        var llmChildren = steps.Where(s => s.Type == StepTypes.LlmCall).ToList();
        Assert.Equal(3, llmChildren.Count);
        Assert.All(llmChildren, c => Assert.NotNull(c.ParentStepId));
        var toolStep = Assert.Single(steps, s => s.Type == StepTypes.ToolCall);
        Assert.Equal(steps.Single(s => s.Type == StepTypes.AgentLlm).StepId, toolStep.ParentStepId);

        // Every step has a StepStarted and a StepCompleted event (steps are not recorded without them).
        var events = harness.Notifier.StepEvents.ToList();
        foreach (var step in steps)
        {
            Assert.Contains(events, e => e.Event == "StepStarted" && e.Step.StepId == step.StepId);
            Assert.Contains(events, e => e.Event == "StepCompleted" && e.Step.StepId == step.StepId && e.Step.SpanId == step.SpanId);
        }

        // LLM and tool calls have their own Mongo documents with trace and span ids.
        Assert.Equal(3, harness.Runs.LlmCalls.Count);
        Assert.All(harness.Runs.LlmCalls, c =>
        {
            Assert.Equal(run.TraceId, c.TraceId);
            Assert.False(string.IsNullOrEmpty(c.SpanId));
            Assert.True(c.CostUsd > 0);
        });
        var toolCall = Assert.Single(harness.Runs.ToolCalls);
        Assert.Equal("control_device", toolCall.Tool);
        Assert.False(string.IsNullOrEmpty(toolCall.SpanId));

        // OTel spans with GenAI names and attributes.
        var names = telemetry.Spans.Select(s => s.DisplayName).ToList();
        Assert.Contains("invoke_agent celiknaz", names);
        Assert.Contains($"chat {TestSettings.DefaultModel}", names);
        Assert.Contains($"chat {TestSettings.FastModel}", names);
        Assert.Contains("execute_tool control_device", names);
        Assert.Contains("step agent.llm", names);
        var chat = telemetry.Spans.First(s => s.DisplayName == $"chat {TestSettings.DefaultModel}");
        Assert.Equal("chat", chat.GetTagItem("gen_ai.operation.name"));
        Assert.Equal("anthropic", chat.GetTagItem("gen_ai.provider.name"));
        Assert.Equal(1000L, chat.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(200L, chat.GetTagItem("gen_ai.usage.output_tokens"));
        var tool = telemetry.Spans.First(s => s.DisplayName == "execute_tool control_device");
        Assert.Equal("control_device", tool.GetTagItem("gen_ai.tool.name"));
        Assert.All(telemetry.Spans.Where(s => s.GetTagItem("beko.run.id") is not null), s => Assert.Equal(run.TraceId, s.TraceId.ToHexString()));

        // Metrics.
        Assert.Contains(telemetry.Of("tool.calls"), m => TelemetryCapture.HasTag(m.Tags, "tool", "control_device") && TelemetryCapture.HasTag(m.Tags, "status", "ok"));
        Assert.Contains(telemetry.Of("gen_ai.cost.usd"), m => TelemetryCapture.HasTag(m.Tags, "agent", "celiknaz") && m.Value > 0);
        Assert.Contains(telemetry.Of("gen_ai.client.token.usage"), m => TelemetryCapture.HasTag(m.Tags, "gen_ai.token.type", "output"));
        Assert.Contains(telemetry.Of("agent.run.duration"), m => TelemetryCapture.HasTag(m.Tags, "agent", "celiknaz"));

        // Daily cost rollup, one entry per LLM call.
        Assert.Equal(3, harness.Rollup.Entries.Count);
    }

    [Fact]
    public async Task RunTotals_SumTokensAndCostOfAllLlmCalls()
    {
        var harness = RunHarness.Create(Script(("t1", "get_home_status", "{}"), "Tamam."), guards: false);

        var run = await harness.RunAsync("Durum");

        Assert.Equal(2000, run.Totals.InputTokens);
        Assert.Equal(400, run.Totals.OutputTokens);
        // 2 calls * (1000 * 2 + 200 * 10) / 1e6 USD
        Assert.Equal(0.008m, run.Totals.CostUsd);
        Assert.Equal(harness.Runs.LlmCalls.Sum(c => c.CostUsd), run.Totals.CostUsd);
        var agentStep = harness.Runs.Steps.Single(s => s.Type == StepTypes.AgentLlm);
        Assert.Equal(0.008m, agentStep.CostUsd);
        Assert.Equal(new StepTokens(2000, 400), agentStep.Tokens);
    }

    [Fact]
    public async Task LlmFailure_FailsRun_SendsStepFailed_AndAnswersWithTurkishFallback()
    {
        var harness = RunHarness.Create(
            (request, _) => request.Model == TestSettings.FastModel
                ? FakeLlmClient.Verdict("ok")
                : throw new InvalidOperationException("overloaded"));

        var run = await harness.RunAsync("Ev soğudu");

        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Equal("Şu anda yanıt veremiyorum, lütfen tekrar deneyin.", WorkflowEngine.FallbackAnswer);

        var events = harness.Notifier.Events.ToList();
        Assert.Contains("StepFailed:llm.call", events);
        Assert.Contains("StepFailed:agent.llm", events);
        Assert.Equal("RunCompleted", events[^1]);
        Assert.Single(events, e => e == "RunCompleted");

        var (text, actions, _) = Assert.Single(harness.Notifier.Completed);
        Assert.Equal(WorkflowEngine.FallbackAnswer, text);
        Assert.Empty(actions);

        var assistant = Assert.Single(harness.Conversations.Messages, m => m.Role == MessageRoles.Assistant);
        Assert.Equal(WorkflowEngine.FallbackAnswer, assistant.Text);
        Assert.Equal(run.RunId, assistant.RunId);

        var failedCall = Assert.Single(harness.Runs.LlmCalls, c => c.Error is not null);
        Assert.Equal("overloaded", failedCall.Error);
        Assert.Contains(harness.Runs.Steps, s => s.Type == StepTypes.LlmCall && s.Status == StepStatuses.Failed && s.Error == "overloaded");
        Assert.DoesNotContain(harness.Runs.Steps, s => s.Type == StepTypes.Respond);
    }

    [Fact]
    public async Task RunRecord_StoresTheUserText_WithPersonalDataMasked()
    {
        var harness = RunHarness.Create(FakeLlmClient.Answering("Tamam."));

        var run = await harness.RunAsync("Merhaba");

        Assert.Equal("Merhaba", run.UserText);
        Assert.Equal(ChatService.DemoUserId, run.UserId);
        Assert.Equal(StepStatuses.Completed, harness.Runs.Steps[0].Status);
    }

    [Fact]
    public async Task StreamedText_IsForwardedAsTextDeltas()
    {
        var harness = RunHarness.Create(FakeLlmClient.Answering("Merhaba!"), guards: false);

        await harness.RunAsync("Selam");

        Assert.Contains("TextDelta", harness.Notifier.Events);
    }

    [Fact]
    public void ControlDeviceTool_SourceIsAi()
    {
        Assert.Equal("ai", ControlDeviceTool.Source);
    }

    private static Func<LlmRequest, int, LlmResponse> Script((string Id, string Tool, string Input) toolCall, string answer) =>
        (request, _) =>
        {
            if (request.Model == TestSettings.FastModel)
            {
                return FakeLlmClient.Verdict("ok");
            }

            var hasToolResult = request.Messages[^1].Content.Any(c => c is LlmToolResult);
            return hasToolResult ? FakeLlmClient.Text(answer) : FakeLlmClient.ToolUse(toolCall.Id, toolCall.Tool, toolCall.Input);
        };
}

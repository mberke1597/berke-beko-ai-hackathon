using System.Diagnostics;
using System.Text.Json;
using Beko.Agents.Application.Agents.Guardrails;
using Beko.Agents.Application.Agents.Llm;
using Beko.Agents.Application.Agents.Tools;
using Beko.Agents.Application.Telemetry;
using Beko.Agents.Domain.Agents;
using Microsoft.Extensions.Logging;

namespace Beko.Agents.Application.Agents.Runs;

/// <summary>
/// The shared step wrapper. Every run, workflow step, LLM call and tool call goes through it and gets
/// (1) an OTel span with GenAI attributes, (2) Mongo documents with <c>trace_id</c>/<c>span_id</c>,
/// (3) RunHub events and (4) metrics, duration, tokens and cost. Executors never see <see cref="ILlmClient"/>
/// or call tools directly: they only get a <see cref="StepScope"/>.
/// </summary>
public sealed partial class RunRecorder(
    IRunStore store,
    IRunNotifier notifier,
    ICostRollup costRollup,
    ILlmClient llm,
    ICostCalculator costCalculator,
    TimeProvider timeProvider,
    ILogger<RunRecorder> logger)
{
    private const int SummaryLength = 120;

    /// <summary>Runs <paramref name="body"/> as one agent run. Exceptions mark the run failed and are not rethrown.</summary>
    public async Task<RunRecord> RunAsync(RunContext context, Func<RunScope, Task> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(body);

        var request = context.Request;
        var agentName = context.Agent?.Name ?? context.AgentId;
        using var activity = BekoTelemetry.ActivitySource.StartActivity(
            $"{GenAiAttributes.InvokeAgent} {context.AgentId}", ActivityKind.Internal, request.Parent);
        activity?.SetTag(GenAiAttributes.OperationName, GenAiAttributes.InvokeAgent);
        activity?.SetTag(GenAiAttributes.ProviderName, GenAiAttributes.Anthropic);
        activity?.SetTag(GenAiAttributes.AgentId, context.AgentId);
        activity?.SetTag(GenAiAttributes.AgentName, agentName);
        activity?.SetTag(GenAiAttributes.ConversationId, context.ConversationId);
        activity?.SetTag(GenAiAttributes.RequestModel, context.Model);
        activity?.SetTag("beko.run.id", context.RunId);
        activity?.SetTag("beko.workflow.version", context.WorkflowVersion);

        context.TraceId = activity?.TraceId.ToHexString()
            ?? (request.Parent.TraceId != default ? request.Parent.TraceId.ToHexString() : ActivityTraceId.CreateRandom().ToHexString());
        var spanId = activity?.SpanId.ToHexString();
        var started = timeProvider.GetTimestamp();
        var startedAt = timeProvider.GetUtcNow().UtcDateTime;

        RunRecord Snapshot(string status, string? error) => new(
            context.RunId,
            context.ConversationId,
            context.AgentId,
            request.UserId,
            request.MemberId,
            context.WorkflowVersion,
            status,
            startedAt,
            (long)timeProvider.GetElapsedTime(started).TotalMilliseconds,
            Totals(context, timeProvider.GetElapsedTime(started)),
            error,
            context.TraceId,
            spanId,
            PiiDetector.Redact(request.UserText));

        await SafeAsync(() => store.SaveRunAsync(Snapshot(RunStatuses.Running, null), cancellationToken), "save run");
        await SafeAsync(() => notifier.RunStartedAsync(context.ConversationId, context.RunId, context.AgentId, context.TraceId, cancellationToken), "RunStarted");

        string status;
        string? error = null;
        try
        {
            await body(new RunScope(this, context));
            status = RunStatuses.Completed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            status = RunStatuses.Failed;
            error = ex.Message;
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            LogRunFailed(logger, ex, context.RunId, context.AgentId);
        }

        var elapsed = timeProvider.GetElapsedTime(started);
        activity?.SetTag(GenAiAttributes.UsageInputTokens, context.Usage.InputTokens);
        activity?.SetTag(GenAiAttributes.UsageOutputTokens, context.Usage.OutputTokens);
        activity?.SetTag("beko.cost_usd", (double)context.CostUsd);
        activity?.SetTag("beko.run.status", status);
        AgentMetrics.RunDuration.Record(
            elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("agent", context.AgentId),
            new KeyValuePair<string, object?>("status", status));

        var run = Snapshot(status, error);
        await SafeAsync(() => store.SaveRunAsync(run, CancellationToken.None), "save run");
        return run;
    }

    /// <summary>Sends <c>RunCompleted</c> with the final answer (also for failed runs, with the fallback text).</summary>
    public Task CompleteAsync(RunContext context, RunRecord run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(run);
        return SafeAsync(
            () => notifier.RunCompletedAsync(context.ConversationId, context.RunId, context.AnswerText ?? string.Empty, [.. context.Actions], run.Totals, cancellationToken),
            "RunCompleted");
    }

    internal async Task ExecuteStepAsync(
        RunContext context,
        string type,
        string name,
        string? parentStepId,
        string spanName,
        Action<Activity>? tagSpan,
        Func<StepScope, Task> body,
        StepScope? parentScope,
        CancellationToken cancellationToken)
    {
        using var activity = BekoTelemetry.ActivitySource.StartActivity(spanName, ActivityKind.Internal);
        activity?.SetTag("beko.step.type", type);
        activity?.SetTag("beko.step.name", name);
        activity?.SetTag("beko.run.id", context.RunId);
        activity?.SetTag(GenAiAttributes.AgentId, context.AgentId);
        activity?.SetTag(GenAiAttributes.ConversationId, context.ConversationId);
        if (activity is not null)
        {
            tagSpan?.Invoke(activity);
        }

        var scope = new StepScope(this, context, type, Guid.NewGuid().ToString("N"), parentScope, cancellationToken);
        var started = timeProvider.GetTimestamp();
        var startedAt = timeProvider.GetUtcNow().UtcDateTime;
        var sequence = context.NextSequence();
        var traceId = activity?.TraceId.ToHexString() ?? context.TraceId;
        var spanId = activity?.SpanId.ToHexString();

        StepRecord Snapshot(string status) => new(
            scope.StepId,
            context.RunId,
            context.ConversationId,
            context.AgentId,
            parentStepId,
            sequence,
            type,
            name,
            status,
            startedAt,
            (long)timeProvider.GetElapsedTime(started).TotalMilliseconds,
            scope.Usage == LlmUsage.Zero && scope.CostUsd == 0 ? null : new StepTokens(scope.Usage.InputTokens, scope.Usage.OutputTokens),
            scope.CostUsd,
            Truncate(scope.Summary ?? name, SummaryLength),
            scope.Input,
            scope.Output,
            scope.Error,
            traceId,
            spanId);

        var running = Snapshot(StepStatuses.Running);
        await SafeAsync(() => store.SaveStepAsync(running, cancellationToken), "save step");
        await SafeAsync(() => notifier.StepStartedAsync(context.ConversationId, context.RunId, running.ToView(), cancellationToken), "StepStarted");

        Exception? failure = null;
        try
        {
            await body(scope);
        }
        catch (Exception ex)
        {
            failure = ex;
            scope.Fail(ex.Message);
            activity?.AddException(ex);
        }

        if (scope.Error is not null)
        {
            activity?.SetStatus(ActivityStatusCode.Error, scope.Error);
            var failed = Snapshot(StepStatuses.Failed);
            await SafeAsync(() => store.SaveStepAsync(failed, CancellationToken.None), "save step");
            await SafeAsync(() => notifier.StepFailedAsync(context.ConversationId, context.RunId, failed.ToView(), scope.Error, CancellationToken.None), "StepFailed");
        }
        else
        {
            var completed = Snapshot(StepStatuses.Completed);
            await SafeAsync(() => store.SaveStepAsync(completed, CancellationToken.None), "save step");
            await SafeAsync(() => notifier.StepCompletedAsync(context.ConversationId, context.RunId, completed.ToView(), CancellationToken.None), "StepCompleted");
        }

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    internal async Task<LlmResponse> CallLlmAsync(StepScope parent, LlmRequest request)
    {
        var context = parent.Context;
        LlmResponse? response = null;
        await ExecuteStepAsync(
            context,
            StepTypes.LlmCall,
            request.Model,
            parent.StepId,
            $"{GenAiAttributes.Chat} {request.Model}",
            span =>
            {
                span.SetTag(GenAiAttributes.OperationName, GenAiAttributes.Chat);
                span.SetTag(GenAiAttributes.ProviderName, GenAiAttributes.Anthropic);
                span.SetTag(GenAiAttributes.System, GenAiAttributes.Anthropic);
                span.SetTag(GenAiAttributes.RequestModel, request.Model);
                span.SetTag(GenAiAttributes.RequestMaxTokens, request.MaxTokens);
            },
            async scope =>
            {
                var started = timeProvider.GetTimestamp();
                scope.Input = $"{request.Messages.Count} messages, {request.Tools.Count} tools";
                var activity = Activity.Current;
                try
                {
                    response = await llm.CompleteAsync(
                        request,
                        delta => SafeAsync(() => notifier.TextDeltaAsync(context.ConversationId, context.RunId, delta, scope.CancellationToken), "TextDelta"),
                        scope.CancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var failedCall = new LlmCallRecord(
                        Guid.NewGuid().ToString("N"), context.RunId, scope.StepId, context.ConversationId, context.AgentId, request.Model,
                        0, 0, 0, 0, 0m, null, (long)timeProvider.GetElapsedTime(started).TotalMilliseconds, ex.Message,
                        timeProvider.GetUtcNow().UtcDateTime, activity?.TraceId.ToHexString() ?? context.TraceId, activity?.SpanId.ToHexString());
                    await SafeAsync(() => store.AddLlmCallAsync(failedCall, CancellationToken.None), "save llm call");
                    throw;
                }

                var elapsed = timeProvider.GetElapsedTime(started);
                var usage = response.Usage;
                var cost = costCalculator.Calculate(request.Model, usage);
                scope.AddUsage(usage, cost);
                scope.Summary = $"{response.Model} · {response.StopReason}";
                scope.Output = Truncate(response.Text, 500);

                activity?.SetTag(GenAiAttributes.ResponseModel, response.Model);
                activity?.SetTag(GenAiAttributes.ResponseFinishReasons, new[] { response.StopReason });
                activity?.SetTag(GenAiAttributes.UsageInputTokens, usage.InputTokens);
                activity?.SetTag(GenAiAttributes.UsageOutputTokens, usage.OutputTokens);
                activity?.SetTag(GenAiAttributes.UsageCacheReadInputTokens, usage.CacheReadTokens);
                activity?.SetTag(GenAiAttributes.UsageCacheCreationInputTokens, usage.CacheWriteTokens);
                activity?.SetTag("beko.cost_usd", (double)cost);

                var modelTags = new TagList
                {
                    { GenAiAttributes.OperationName, GenAiAttributes.Chat },
                    { GenAiAttributes.ProviderName, GenAiAttributes.Anthropic },
                    { GenAiAttributes.RequestModel, request.Model },
                };
                var inputTags = modelTags;
                inputTags.Add(GenAiAttributes.TokenType, "input");
                var outputTags = modelTags;
                outputTags.Add(GenAiAttributes.TokenType, "output");
                AgentMetrics.TokenUsage.Record(usage.InputTokens + usage.CacheReadTokens + usage.CacheWriteTokens, inputTags);
                AgentMetrics.TokenUsage.Record(usage.OutputTokens, outputTags);
                AgentMetrics.OperationDuration.Record(elapsed.TotalSeconds, modelTags);
                AgentMetrics.CostUsd.Add(
                    (double)cost,
                    new KeyValuePair<string, object?>("agent", context.AgentId),
                    new KeyValuePair<string, object?>("model", request.Model));

                var call = new LlmCallRecord(
                    Guid.NewGuid().ToString("N"), context.RunId, scope.StepId, context.ConversationId, context.AgentId, request.Model,
                    usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheWriteTokens, cost, response.StopReason,
                    (long)elapsed.TotalMilliseconds, null, timeProvider.GetUtcNow().UtcDateTime,
                    activity?.TraceId.ToHexString() ?? context.TraceId, activity?.SpanId.ToHexString());
                await SafeAsync(() => store.AddLlmCallAsync(call, scope.CancellationToken), "save llm call");
                await SafeAsync(
                    () => costRollup.AddAsync(DateOnly.FromDateTime(call.AtUtc), context.AgentId, request.Model, usage, cost, scope.CancellationToken),
                    "cost rollup");
            },
            parent,
            parent.CancellationToken);

        return response!;
    }

    internal async Task<ToolExecutionResult> CallToolAsync(StepScope parent, IAgentTool tool, LlmToolUse toolUse)
    {
        var context = parent.Context;
        var inputJson = toolUse.Input.ValueKind == JsonValueKind.Undefined ? "{}" : toolUse.Input.GetRawText();
        ToolExecutionResult result = new("""{"error":"tool_not_run"}""", true);
        await ExecuteStepAsync(
            context,
            StepTypes.ToolCall,
            tool.Name,
            parent.StepId,
            $"{GenAiAttributes.ExecuteTool} {tool.Name}",
            span =>
            {
                span.SetTag(GenAiAttributes.OperationName, GenAiAttributes.ExecuteTool);
                span.SetTag(GenAiAttributes.ToolName, tool.Name);
                span.SetTag(GenAiAttributes.ToolCallId, toolUse.Id);
                span.SetTag(GenAiAttributes.ToolType, "function");
            },
            async scope =>
            {
                var started = timeProvider.GetTimestamp();
                var activity = Activity.Current;
                scope.Input = inputJson;
                try
                {
                    var toolContext = new ToolContext(context.RunId, context.ConversationId, context.AgentId, context.Request.MemberId);
                    result = await tool.ExecuteAsync(toolUse.Input, toolContext, scope.CancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // An unexpected tool failure goes back to the model as an error result; the run continues.
                    result = new ToolExecutionResult(JsonSerializer.Serialize(new { error = "tool_failed" }), true);
                    scope.Fail(ex.Message);
                    activity?.AddException(ex);
                }

                var status = result.IsError ? "error" : "ok";
                scope.Output = Truncate(result.Content, 2000);
                scope.Summary = $"{tool.Name} {Truncate(inputJson, 80)}{(result.IsError ? " → error" : string.Empty)}";
                if (result.Action is not null)
                {
                    context.Actions.Add(result.Action);
                }

                activity?.SetTag("beko.tool.status", status);
                AgentMetrics.ToolCalls.Add(
                    1,
                    new KeyValuePair<string, object?>("tool", tool.Name),
                    new KeyValuePair<string, object?>("status", status));

                var call = new ToolCallRecord(
                    Guid.NewGuid().ToString("N"), context.RunId, scope.StepId, context.ConversationId, context.AgentId, tool.Name, toolUse.Id,
                    inputJson, result.Content, result.IsError, (long)timeProvider.GetElapsedTime(started).TotalMilliseconds,
                    timeProvider.GetUtcNow().UtcDateTime, activity?.TraceId.ToHexString() ?? context.TraceId, activity?.SpanId.ToHexString());
                await SafeAsync(() => store.AddToolCallAsync(call, scope.CancellationToken), "save tool call");
            },
            parent,
            parent.CancellationToken);

        return result;
    }

    internal async Task RecordGuardrailAsync(StepScope scope, string stage, string verdict, bool blocked, string reason, string detector, long latencyMs)
    {
        var context = scope.Context;
        var activity = Activity.Current;
        activity?.SetTag("beko.guardrail.stage", stage);
        activity?.SetTag("beko.guardrail.verdict", verdict);
        activity?.SetTag("beko.guardrail.blocked", blocked);
        activity?.SetTag("beko.guardrail.detector", detector);
        if (blocked)
        {
            AgentMetrics.GuardrailBlocks.Add(
                1,
                new KeyValuePair<string, object?>("reason", verdict),
                new KeyValuePair<string, object?>("stage", stage));
        }

        var decision = new GuardrailDecisionRecord(
            Guid.NewGuid().ToString("N"), context.RunId, scope.StepId, context.ConversationId, context.AgentId, stage, verdict, blocked,
            reason, detector, latencyMs, timeProvider.GetUtcNow().UtcDateTime,
            activity?.TraceId.ToHexString() ?? context.TraceId, activity?.SpanId.ToHexString());
        await SafeAsync(() => store.AddGuardrailDecisionAsync(decision, scope.CancellationToken), "save guardrail decision");
    }

    private static RunTotals Totals(RunContext context, TimeSpan elapsed) => new(
        context.Usage.InputTokens,
        context.Usage.OutputTokens,
        context.Usage.CacheReadTokens,
        context.Usage.CacheWriteTokens,
        context.CostUsd,
        (long)elapsed.TotalMilliseconds);

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : string.Concat(value.AsSpan(0, length - 1), "…");

    /// <summary>Recording must never break a run: store and notifier failures are logged.</summary>
    private async Task SafeAsync(Func<Task> action, string what)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRecordingFailed(logger, ex, what);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Run {RunId} of agent {AgentId} failed")]
    private static partial void LogRunFailed(ILogger logger, Exception exception, string runId, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording failed: {What}")]
    private static partial void LogRecordingFailed(ILogger logger, Exception exception, string what);
}

/// <summary>Handle an executor gets for the run: runs (nested) workflow steps through the wrapper.</summary>
public sealed class RunScope
{
    private readonly RunRecorder _recorder;

    internal RunScope(RunRecorder recorder, RunContext context)
    {
        _recorder = recorder;
        Context = context;
    }

    public RunContext Context { get; }

    /// <summary>Runs one workflow step. Exceptions mark the step failed and are rethrown.</summary>
    public Task RunStepAsync(string type, string name, string? parentStepId, Func<StepScope, Task> body, CancellationToken cancellationToken) =>
        _recorder.ExecuteStepAsync(Context, type, name, parentStepId, $"step {type}", null, body, null, cancellationToken);
}

/// <summary>
/// Handle a step body gets. LLM and tool calls are only possible through it, so they are always recorded as child steps.
/// </summary>
public sealed class StepScope
{
    private readonly RunRecorder _recorder;
    private readonly StepScope? _parent;

    internal StepScope(RunRecorder recorder, RunContext context, string type, string stepId, StepScope? parent, CancellationToken cancellationToken)
    {
        _recorder = recorder;
        _parent = parent;
        Context = context;
        Type = type;
        StepId = stepId;
        CancellationToken = cancellationToken;
    }

    public RunContext Context { get; }

    public string Type { get; }

    public string StepId { get; }

    public CancellationToken CancellationToken { get; }

    /// <summary>Short human-readable line shown on the step card.</summary>
    public string? Summary { get; set; }

    public string? Input { get; set; }

    public string? Output { get; set; }

    public string? Error { get; private set; }

    public LlmUsage Usage { get; private set; } = LlmUsage.Zero;

    public decimal CostUsd { get; private set; }

    /// <summary>Marks the step failed without throwing (the wrapper sends <c>StepFailed</c>).</summary>
    public void Fail(string error) => Error = error;

    /// <summary>One model call, recorded as an <c>llm.call</c> child step with a <c>chat {model}</c> span.</summary>
    public Task<LlmResponse> CallLlmAsync(LlmRequest request) => _recorder.CallLlmAsync(this, request);

    /// <summary>One tool execution, recorded as a <c>tool.call</c> child step with an <c>execute_tool {tool}</c> span.</summary>
    public Task<ToolExecutionResult> CallToolAsync(IAgentTool tool, LlmToolUse toolUse) => _recorder.CallToolAsync(this, tool, toolUse);

    /// <summary>
    /// Records a guardrail decision (Mongo <c>guardrail_decisions</c> with trace and span ids, span tags and, for a block,
    /// the <c>guardrail_blocks_total{reason}</c> metric). The only way for a guard step to record a verdict.
    /// </summary>
    public Task RecordGuardrailAsync(string stage, string verdict, bool blocked, string reason, string detector, long latencyMs) =>
        _recorder.RecordGuardrailAsync(this, stage, verdict, blocked, reason, detector, latencyMs);

    internal void AddUsage(LlmUsage usage, decimal costUsd)
    {
        Usage += usage;
        CostUsd += costUsd;
        if (_parent is not null)
        {
            _parent.AddUsage(usage, costUsd);
        }
        else
        {
            Context.AddUsage(usage, costUsd);
        }
    }
}

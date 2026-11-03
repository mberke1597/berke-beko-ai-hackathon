using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Beko.Agents.Application.Agents.Guardrails;
using Beko.Agents.Application.Agents.Llm;
using Beko.Agents.Application.Agents.Runs;
using Beko.Agents.Application.Agents.Tools;
using Beko.Agents.Application.Options;
using Beko.Agents.Domain.Agents;
using Microsoft.Extensions.Options;

namespace Beko.Agents.Application.Agents.Workflow;

/// <summary><c>trigger.user_message</c>: records the incoming message and loads the conversation history.</summary>
public sealed class UserMessageTriggerExecutor(IConversationStore conversations) : IStepExecutor
{
    /// <summary>Earlier messages sent to the model with each run.</summary>
    public const int MaxHistoryMessages = 20;

    public string Type => StepTypes.TriggerUserMessage;

    public async Task ExecuteAsync(RunContext context, WorkflowStep step, StepScope scope, RunScope run)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(scope);

        var messages = await conversations.GetMessagesAsync(context.ConversationId, scope.CancellationToken);
        var current = messages.ToList().FindIndex(m => m.MessageId == context.Request.UserMessageId);
        var earlier = (current >= 0 ? messages.Take(current) : messages)
            .Where(m => !string.IsNullOrWhiteSpace(m.Text))
            .TakeLast(MaxHistoryMessages);

        foreach (var message in earlier)
        {
            var role = message.Role == MessageRoles.Assistant ? LlmRoles.Assistant : LlmRoles.User;
            if (context.History.Count > 0 && context.History[^1].Role == role)
            {
                // Keep strict user/assistant alternation by merging consecutive messages of the same role.
                var previous = context.History[^1];
                context.History[^1] = previous with { Content = [.. previous.Content, new LlmText(message.Text)] };
            }
            else
            {
                context.History.Add(new LlmMessage(role, [new LlmText(message.Text)]));
            }
        }

        if (context.History.Count > 0 && context.History[0].Role == LlmRoles.Assistant)
        {
            context.History.RemoveAt(0);
        }

        var shown = PiiDetector.Redact(context.Request.UserText);
        scope.Input = shown;
        scope.Summary = $"{context.Request.Channel}: {shown}";
        scope.Output = $"{context.History.Count} earlier messages";
    }
}

/// <summary>
/// <c>agent.llm</c>: the Claude tool-use loop. Each model call and tool execution is a child step.
/// Config: <c>{ "maxIterations": 6, "maxTokens": 1024 }</c>.
/// </summary>
public sealed class AgentLlmExecutor(
    IAgentRepository agents,
    IToolRegistry tools,
    IOptions<AnthropicOptions> anthropic,
    TimeProvider timeProvider) : IStepExecutor
{
    public const int DefaultMaxIterations = 6;

    /// <summary>Answer when the loop hits its iteration cap without a final text.</summary>
    public const string IterationCapAnswer = "İsteğini tamamlayamadım, lütfen daha kısa bir istekle tekrar dener misin?";

    public string Type => StepTypes.AgentLlm;

    public async Task ExecuteAsync(RunContext context, WorkflowStep step, StepScope scope, RunScope run)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(scope);

        if (context.Blocked)
        {
            scope.Summary = "Atlandı (güvenlik filtresi)";
            return;
        }

        var agent = context.Agent ?? throw new InvalidOperationException("The run has no agent.");
        var config = AgentLlmConfig.Parse(step.Config, anthropic.Value.MaxTokens);
        var agentTools = agent.Tools
            .Select(t => tools.Find(t.ToolName))
            .OfType<IAgentTool>()
            .ToDictionary(t => t.Name, StringComparer.Ordinal);
        var toolDefinitions = agentTools.Values.Select(t => new LlmToolDefinition(t.Name, t.Description, t.InputSchema)).ToList();
        var system = await BuildSystemPromptAsync(context, agent, scope.CancellationToken);

        var messages = new List<LlmMessage>(context.History) { LlmMessage.UserText(context.Request.UserText) };
        string? answer = null;
        var iterations = 0;
        while (iterations < config.MaxIterations)
        {
            iterations++;
            var response = await scope.CallLlmAsync(new LlmRequest(context.Model, system, [.. messages], toolDefinitions, config.MaxTokens));
            messages.Add(new LlmMessage(LlmRoles.Assistant, response.Content));

            var toolUses = response.ToolUses;
            if (response.StopReason != StopReasons.ToolUse || toolUses.Count == 0)
            {
                answer = response.Text.Trim();
                break;
            }

            var results = new List<LlmContent>();
            foreach (var toolUse in toolUses)
            {
                if (!agentTools.TryGetValue(toolUse.Name, out var tool))
                {
                    results.Add(new LlmToolResult(toolUse.Id, JsonSerializer.Serialize(new { error = "unknown_tool", tool = toolUse.Name }), true));
                    continue;
                }

                var result = await scope.CallToolAsync(tool, toolUse);
                results.Add(new LlmToolResult(toolUse.Id, result.Content, result.IsError));
            }

            messages.Add(new LlmMessage(LlmRoles.User, results));
        }

        context.AnswerText = string.IsNullOrWhiteSpace(answer) ? IterationCapAnswer : answer;
        scope.Input = PiiDetector.Redact(context.Request.UserText);
        scope.Output = context.AnswerText;
        scope.Summary = $"{context.Model} · {iterations} calls · {context.Actions.Count} actions";
        Activity.Current?.SetTag("beko.agent.iterations", iterations);
    }

    private async Task<string> BuildSystemPromptAsync(RunContext context, Agent agent, CancellationToken cancellationToken)
    {
        var parts = new List<string> { agent.Instructions };
        if (agent.TonePresetId is { } toneId && await agents.GetTonePresetAsync(toneId, cancellationToken) is { } tone)
        {
            parts.Add($"Üslup ({tone.Name}): {tone.Instructions}");
        }

        var now = timeProvider.GetUtcNow().ToOffset(TimeSpan.FromHours(3));
        parts.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"Bağlam: konuşan ev üyesi kimliği '{context.Request.MemberId}', kanal '{context.Request.Channel}', İstanbul saati {now:yyyy-MM-dd HH:mm}."));
        return string.Join("\n\n", parts);
    }
}

/// <summary><c>agent.llm</c> step configuration.</summary>
public sealed record AgentLlmConfig(int MaxIterations, int MaxTokens)
{
    public static AgentLlmConfig Parse(string? json, int defaultMaxTokens)
    {
        var maxIterations = AgentLlmExecutor.DefaultMaxIterations;
        var maxTokens = defaultMaxTokens;
        if (!string.IsNullOrWhiteSpace(json))
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("maxIterations", out var iterations) && iterations.TryGetInt32(out var i) && i > 0)
                {
                    maxIterations = i;
                }

                if (root.TryGetProperty("maxTokens", out var tokens) && tokens.TryGetInt32(out var t) && t > 0)
                {
                    maxTokens = t;
                }
            }
        }

        return new AgentLlmConfig(maxIterations, maxTokens);
    }
}

/// <summary><c>respond</c>: stores the answer and its actions as the assistant message.</summary>
public sealed class RespondExecutor(IConversationStore conversations, TimeProvider timeProvider) : IStepExecutor
{
    public string Type => StepTypes.Respond;

    public async Task ExecuteAsync(RunContext context, WorkflowStep step, StepScope scope, RunScope run)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(scope);

        var text = string.IsNullOrWhiteSpace(context.AnswerText) ? WorkflowEngine.FallbackAnswer : context.AnswerText;
        context.AnswerText = text;
        var activity = Activity.Current;
        await conversations.AddMessageAsync(
            new MessageRecord(
                Guid.NewGuid().ToString("N"),
                context.ConversationId,
                context.AgentId,
                context.Request.UserId,
                MessageRoles.Assistant,
                text,
                context.Request.Channel,
                context.RunId,
                timeProvider.GetUtcNow().UtcDateTime,
                [.. context.Actions],
                activity?.TraceId.ToHexString() ?? context.TraceId,
                activity?.SpanId.ToHexString()),
            scope.CancellationToken);

        scope.Output = text;
        scope.Summary = text.Length <= 80 ? text : string.Concat(text.AsSpan(0, 79), "…");
    }
}

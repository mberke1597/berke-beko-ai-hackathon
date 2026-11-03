using System.Diagnostics;
using Beko.Agents.Application.Agents.Guardrails;
using Beko.Agents.Application.Agents.Runs;
using Beko.Agents.Application.Agents.Tools;
using Beko.Agents.Application.Homes;
using Beko.Agents.Application.Options;
using Beko.Agents.Domain.Agents;
using Microsoft.Extensions.Options;

namespace Beko.Agents.Application.Agents;

/// <summary>Background run queue (bounded, in process).</summary>
public interface IRunDispatcher
{
    /// <summary>Queues a run; <c>false</c> when the queue is full.</summary>
    bool TryEnqueue(RunRequest request);
}

/// <summary>Agents, conversations, messages and runs as the API exposes them.</summary>
public sealed class ChatService(
    IAgentRepository agents,
    IConversationStore conversations,
    IRunStore runs,
    IRunDispatcher queue,
    IToolRegistry tools,
    IOptions<AnthropicOptions> anthropic,
    TimeProvider timeProvider)
{
    /// <summary>Until auth exists (Phase 4) every caller is this user.</summary>
    public const string DemoUserId = "demo-user";

    public async Task<IReadOnlyList<AgentSummary>> GetAgentsAsync(CancellationToken cancellationToken) =>
        [.. (await agents.GetAgentsAsync(cancellationToken)).Select(a => new AgentSummary(a.Id, a.Name, a.Description, ModelOf(a)))];

    public async Task<AgentDetails?> GetAgentAsync(string agentId, CancellationToken cancellationToken)
    {
        var agent = await agents.GetAgentAsync(agentId, cancellationToken);
        if (agent is null)
        {
            return null;
        }

        var workflow = await agents.GetActiveWorkflowAsync(agent.Id, cancellationToken);
        return new AgentDetails(
            agent.Id,
            agent.Name,
            agent.Description,
            ModelOf(agent),
            agent.Instructions,
            [.. agent.Tools.Select(t => new AgentToolSummary(t.ToolName, t.Tool?.Description ?? tools.Find(t.ToolName)?.Description ?? string.Empty))],
            [.. agent.Subagents.Select(s => s.SubagentId)],
            workflow?.Version ?? 0);
    }

    /// <summary>Starts a conversation, or returns <c>null</c> when the agent does not exist.</summary>
    public async Task<ConversationRecord?> CreateConversationAsync(string agentId, string? memberId, CancellationToken cancellationToken)
    {
        if (await agents.GetAgentAsync(agentId, cancellationToken) is null)
        {
            return null;
        }

        var activity = Activity.Current;
        var conversation = new ConversationRecord(
            Guid.NewGuid().ToString("N"),
            agentId,
            DemoUserId,
            string.IsNullOrWhiteSpace(memberId) ? DemoHome.DefaultMemberId : memberId,
            timeProvider.GetUtcNow().UtcDateTime,
            activity?.TraceId.ToHexString(),
            activity?.SpanId.ToHexString());
        await conversations.AddConversationAsync(conversation, cancellationToken);
        return conversation;
    }

    public async Task<IReadOnlyList<MessageRecord>?> GetMessagesAsync(string conversationId, CancellationToken cancellationToken) =>
        await conversations.GetConversationAsync(conversationId, cancellationToken) is null
            ? null
            : await conversations.GetMessagesAsync(conversationId, cancellationToken);

    /// <summary>
    /// Stores the user message and queues the run. The run's spans are children of the current request span,
    /// so the returned trace id is known before the run starts.
    /// </summary>
    public async Task<PostMessageOutcome> PostMessageAsync(string conversationId, string text, string channel, CancellationToken cancellationToken)
    {
        var conversation = await conversations.GetConversationAsync(conversationId, cancellationToken);
        if (conversation is null)
        {
            return PostMessageOutcome.NotFound;
        }

        var activity = Activity.Current;
        var parent = activity?.Context ?? new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
        var runId = Guid.NewGuid().ToString("N");
        var message = new MessageRecord(
            Guid.NewGuid().ToString("N"),
            conversationId,
            conversation.AgentId,
            conversation.UserId,
            MessageRoles.User,
            PiiDetector.Redact(text),
            channel,
            runId,
            timeProvider.GetUtcNow().UtcDateTime,
            [],
            parent.TraceId.ToHexString(),
            parent.SpanId.ToHexString());
        await conversations.AddMessageAsync(message, cancellationToken);

        var request = new RunRequest(runId, conversationId, conversation.AgentId, conversation.UserId, conversation.MemberId, message.MessageId, text, channel, parent);
        return queue.TryEnqueue(request)
            ? new PostMessageOutcome(runId, parent.TraceId.ToHexString(), false)
            : new PostMessageOutcome(null, null, true);
    }

    /// <summary>The most recent runs for the dashboard, newest first (<paramref name="limit"/> is capped at 200).</summary>
    public async Task<IReadOnlyList<RunSummary>> GetRecentRunsAsync(int limit, CancellationToken cancellationToken)
    {
        var runList = await runs.GetRecentRunsAsync(Math.Clamp(limit, 1, 200), cancellationToken);
        return [.. runList.Select(r => new RunSummary(r.RunId, r.ConversationId, r.AgentId, r.Status, r.StartedAtUtc, r.DurationMs, r.Totals.CostUsd, r.TraceId, r.UserText))];
    }

    public async Task<RunDetails?> GetRunAsync(string runId, CancellationToken cancellationToken)
    {
        var run = await runs.GetRunAsync(runId, cancellationToken);
        if (run is null)
        {
            return null;
        }

        var steps = await runs.GetStepsAsync(runId, cancellationToken);
        return new RunDetails(
            run.RunId,
            run.ConversationId,
            run.AgentId,
            run.WorkflowVersion,
            run.Status,
            run.TraceId,
            run.StartedAtUtc,
            run.DurationMs,
            run.Totals,
            [.. steps.Select(s => s.ToView())]);
    }

    private string ModelOf(Agent agent) => string.IsNullOrWhiteSpace(agent.Model) ? anthropic.Value.DefaultModel : agent.Model;
}

public sealed record AgentSummary(string Id, string Name, string Description, string Model);

public sealed record AgentToolSummary(string Name, string Description);

public sealed record AgentDetails(
    string Id,
    string Name,
    string Description,
    string Model,
    string Instructions,
    IReadOnlyList<AgentToolSummary> Tools,
    IReadOnlyList<string> Subagents,
    int WorkflowVersion);

public sealed record RunSummary(
    string RunId,
    string ConversationId,
    string AgentId,
    string Status,
    DateTime StartedAtUtc,
    long DurationMs,
    decimal CostUsd,
    string? TraceId,
    string? UserText);

public sealed record RunDetails(
    string RunId,
    string ConversationId,
    string AgentId,
    int WorkflowVersion,
    string Status,
    string? TraceId,
    DateTime StartedAtUtc,
    long DurationMs,
    RunTotals Totals,
    IReadOnlyList<StepView> Steps);

/// <summary>Result of posting a message: queued (<see cref="RunId"/>), conversation not found, or queue full.</summary>
public sealed record PostMessageOutcome(string? RunId, string? TraceId, bool QueueFull)
{
    public static readonly PostMessageOutcome NotFound = new(null, null, false);
}

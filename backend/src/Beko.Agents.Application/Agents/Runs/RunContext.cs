using System.Diagnostics;
using Beko.Agents.Application.Agents.Llm;
using Beko.Agents.Domain.Agents;

namespace Beko.Agents.Application.Agents.Runs;

/// <summary>A queued request to run an agent on one user message.</summary>
public sealed record RunRequest(
    string RunId,
    string ConversationId,
    string AgentId,
    string UserId,
    string MemberId,
    string UserMessageId,
    string UserText,
    string Channel,
    ActivityContext Parent);

/// <summary>Mutable state of one run, shared by its steps.</summary>
public sealed class RunContext(RunRequest request, Agent? agent, string model, int workflowVersion)
{
    private int _sequence;

    public RunRequest Request { get; } = request;

    public string RunId => Request.RunId;

    public string ConversationId => Request.ConversationId;

    public string AgentId => Request.AgentId;

    /// <summary>The agent; <c>null</c> only when it does not exist (the run then fails).</summary>
    public Agent? Agent { get; } = agent;

    public string Model { get; } = model;

    public int WorkflowVersion { get; } = workflowVersion;

    /// <summary>Earlier messages of the conversation, set by <c>trigger.user_message</c>.</summary>
    public List<LlmMessage> History { get; } = [];

    /// <summary>The answer to send, set by <c>agent.llm</c> (or the fallback when the run fails).</summary>
    public string? AnswerText { get; set; }

    public List<AgentAction> Actions { get; } = [];

    /// <summary>Set by a guard step that blocked the message; later steps that call the model skip themselves.</summary>
    public bool Blocked { get; set; }

    public LlmUsage Usage { get; private set; } = LlmUsage.Zero;

    public decimal CostUsd { get; private set; }

    /// <summary>Free-form values for executors added later (guardrail verdicts, knowledge hits, ...).</summary>
    public Dictionary<string, object?> Items { get; } = new(StringComparer.Ordinal);

    public string TraceId { get; internal set; } = string.Empty;

    internal int NextSequence() => Interlocked.Increment(ref _sequence);

    internal void AddUsage(LlmUsage usage, decimal costUsd)
    {
        Usage += usage;
        CostUsd += costUsd;
    }
}

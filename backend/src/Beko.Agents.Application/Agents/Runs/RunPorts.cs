using Beko.Agents.Domain.Agents;

namespace Beko.Agents.Application.Agents.Runs;

/// <summary>Agent configuration (Postgres).</summary>
public interface IAgentRepository
{
    Task<IReadOnlyList<Agent>> GetAgentsAsync(CancellationToken cancellationToken);

    /// <summary>The agent with its tools and subagents, or <c>null</c>.</summary>
    Task<Agent?> GetAgentAsync(string agentId, CancellationToken cancellationToken);

    /// <summary>The active workflow version of the agent with its steps, or <c>null</c>.</summary>
    Task<WorkflowDefinition?> GetActiveWorkflowAsync(string agentId, CancellationToken cancellationToken);

    Task<TonePreset?> GetTonePresetAsync(string tonePresetId, CancellationToken cancellationToken);
}

/// <summary>Conversations and messages (Mongo).</summary>
public interface IConversationStore
{
    Task AddConversationAsync(ConversationRecord conversation, CancellationToken cancellationToken);

    Task<ConversationRecord?> GetConversationAsync(string conversationId, CancellationToken cancellationToken);

    Task AddMessageAsync(MessageRecord message, CancellationToken cancellationToken);

    /// <summary>Messages of the conversation, oldest first.</summary>
    Task<IReadOnlyList<MessageRecord>> GetMessagesAsync(string conversationId, CancellationToken cancellationToken);
}

/// <summary>Runs, steps, LLM calls and tool calls (Mongo).</summary>
public interface IRunStore
{
    Task SaveRunAsync(RunRecord run, CancellationToken cancellationToken);

    Task SaveStepAsync(StepRecord step, CancellationToken cancellationToken);

    Task AddLlmCallAsync(LlmCallRecord call, CancellationToken cancellationToken);

    Task AddToolCallAsync(ToolCallRecord call, CancellationToken cancellationToken);

    Task AddGuardrailDecisionAsync(GuardrailDecisionRecord decision, CancellationToken cancellationToken);

    Task<RunRecord?> GetRunAsync(string runId, CancellationToken cancellationToken);

    /// <summary>The most recent runs, newest first.</summary>
    Task<IReadOnlyList<RunRecord>> GetRecentRunsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Steps of the run in execution order.</summary>
    Task<IReadOnlyList<StepRecord>> GetStepsAsync(string runId, CancellationToken cancellationToken);
}

/// <summary>Daily cost rollup (Postgres <c>cost_daily</c>).</summary>
public interface ICostRollup
{
    Task AddAsync(DateOnly day, string agentId, string model, Llm.LlmUsage usage, decimal costUsd, CancellationToken cancellationToken);
}

/// <summary>Pushes run progress to clients (SignalR <c>RunHub</c>, group <c>conversation:{id}</c>).</summary>
public interface IRunNotifier
{
    Task RunStartedAsync(string conversationId, string runId, string agentId, string traceId, CancellationToken cancellationToken);

    Task StepStartedAsync(string conversationId, string runId, StepView step, CancellationToken cancellationToken);

    Task StepCompletedAsync(string conversationId, string runId, StepView step, CancellationToken cancellationToken);

    Task StepFailedAsync(string conversationId, string runId, StepView step, string error, CancellationToken cancellationToken);

    Task TextDeltaAsync(string conversationId, string runId, string delta, CancellationToken cancellationToken);

    Task RunCompletedAsync(string conversationId, string runId, string text, IReadOnlyList<AgentAction> actions, RunTotals totals, CancellationToken cancellationToken);
}

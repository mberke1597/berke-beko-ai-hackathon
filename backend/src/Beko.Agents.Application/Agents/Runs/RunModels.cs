namespace Beko.Agents.Application.Agents.Runs;

public static class RunStatuses
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public static class StepStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

/// <summary>Message roles stored in Mongo <c>messages</c>.</summary>
public static class MessageRoles
{
    public const string User = "user";
    public const string Assistant = "assistant";
}

/// <summary>
/// One <c>control_device</c> call as shown with the assistant answer. <see cref="Accepted"/> is false when the
/// device rejected the command (422).
/// </summary>
public sealed record AgentAction(string ApplianceId, string DisplayName, string Command, object? Value, bool Accepted);

/// <summary>Run totals (contract <c>Totals</c>).</summary>
public sealed record RunTotals(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, decimal CostUsd, long DurationMs)
{
    public static readonly RunTotals Empty = new(0, 0, 0, 0, 0m, 0);
}

public sealed record StepTokens(long Input, long Output);

/// <summary>A step as the API and RunHub show it (contract <c>Step</c>).</summary>
public sealed record StepView(
    string StepId,
    string? ParentStepId,
    string Type,
    string Name,
    string Status,
    DateTime StartedAtUtc,
    long DurationMs,
    StepTokens? Tokens,
    decimal CostUsd,
    string Summary,
    string? SpanId);

/// <summary>Mongo <c>conversations</c>.</summary>
public sealed record ConversationRecord(
    string ConversationId,
    string AgentId,
    string UserId,
    string MemberId,
    DateTime CreatedAtUtc,
    string? TraceId,
    string? SpanId);

/// <summary>Mongo <c>messages</c>.</summary>
public sealed record MessageRecord(
    string MessageId,
    string ConversationId,
    string AgentId,
    string UserId,
    string Role,
    string Text,
    string Channel,
    string? RunId,
    DateTime CreatedAtUtc,
    IReadOnlyList<AgentAction> Actions,
    string? TraceId,
    string? SpanId);

/// <summary>Mongo <c>runs</c>; written when the run starts and again when it ends.</summary>
public sealed record RunRecord(
    string RunId,
    string ConversationId,
    string AgentId,
    string UserId,
    string MemberId,
    int WorkflowVersion,
    string Status,
    DateTime StartedAtUtc,
    long DurationMs,
    RunTotals Totals,
    string? Error,
    string? TraceId,
    string? SpanId,
    string? UserText);

/// <summary>Mongo <c>run_steps</c>; written when the step starts and again when it ends.</summary>
public sealed record StepRecord(
    string StepId,
    string RunId,
    string ConversationId,
    string AgentId,
    string? ParentStepId,
    int Sequence,
    string Type,
    string Name,
    string Status,
    DateTime StartedAtUtc,
    long DurationMs,
    StepTokens? Tokens,
    decimal CostUsd,
    string Summary,
    string? Input,
    string? Output,
    string? Error,
    string? TraceId,
    string? SpanId)
{
    public StepView ToView() => new(StepId, ParentStepId, Type, Name, Status, StartedAtUtc, DurationMs, Tokens, CostUsd, Summary, SpanId);
}

/// <summary>Mongo <c>llm_calls</c>.</summary>
public sealed record LlmCallRecord(
    string CallId,
    string RunId,
    string StepId,
    string ConversationId,
    string AgentId,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    decimal CostUsd,
    string? StopReason,
    long DurationMs,
    string? Error,
    DateTime AtUtc,
    string? TraceId,
    string? SpanId);

/// <summary>Mongo <c>tool_calls</c>.</summary>
public sealed record ToolCallRecord(
    string CallId,
    string RunId,
    string StepId,
    string ConversationId,
    string AgentId,
    string Tool,
    string ToolUseId,
    string Input,
    string Output,
    bool IsError,
    long DurationMs,
    DateTime AtUtc,
    string? TraceId,
    string? SpanId);

/// <summary>Mongo <c>guardrail_decisions</c>: one document per guardrail check.</summary>
public sealed record GuardrailDecisionRecord(
    string DecisionId,
    string RunId,
    string StepId,
    string ConversationId,
    string AgentId,
    string Stage,
    string Verdict,
    bool Blocked,
    string Reason,
    string Detector,
    long LatencyMs,
    DateTime AtUtc,
    string? TraceId,
    string? SpanId);

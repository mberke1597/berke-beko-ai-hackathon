namespace Beko.Agents.Domain.Agents;

/// <summary>An agent configured as data (Postgres <c>agents</c>).</summary>
public sealed class Agent
{
    public required string Id { get; set; }

    public required string Name { get; set; }

    public string Description { get; set; } = string.Empty;

    /// <summary>Model id; <c>null</c> means the configured default model.</summary>
    public string? Model { get; set; }

    public string Instructions { get; set; } = string.Empty;

    public string? TonePresetId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public List<AgentTool> Tools { get; } = [];

    public List<AgentSubagent> Subagents { get; } = [];
}

/// <summary>A tool in the catalogue (Postgres <c>tools</c>). Its implementation is resolved by <see cref="Name"/>.</summary>
public sealed class ToolDefinition
{
    public required string Name { get; set; }

    public string Description { get; set; } = string.Empty;

    /// <summary>JSON schema of the tool input (jsonb).</summary>
    public string InputSchema { get; set; } = "{}";

    public bool Enabled { get; set; } = true;
}

public sealed class AgentTool
{
    public required string AgentId { get; set; }

    public required string ToolName { get; set; }

    public ToolDefinition? Tool { get; set; }
}

public sealed class AgentSubagent
{
    public required string ParentAgentId { get; set; }

    public required string SubagentId { get; set; }
}

/// <summary>Tone of voice injected into the system prompt (Postgres <c>tone_presets</c>).</summary>
public sealed class TonePreset
{
    public required string Id { get; set; }

    public required string Name { get; set; }

    public string Instructions { get; set; } = string.Empty;
}

/// <summary>A versioned workflow of an agent. Editing creates a new version; runs store the version they used.</summary>
public sealed class WorkflowDefinition
{
    public Guid Id { get; set; }

    public required string AgentId { get; set; }

    public int Version { get; set; }

    public bool IsActive { get; set; }

    public string CreatedBy { get; set; } = "system";

    public DateTime CreatedAtUtc { get; set; }

    public List<WorkflowStep> Steps { get; } = [];
}

/// <summary>One step of a workflow definition. <see cref="ParentStepId"/> nests steps (subagent branches).</summary>
public sealed class WorkflowStep
{
    public Guid Id { get; set; }

    public Guid DefinitionId { get; set; }

    public Guid? ParentStepId { get; set; }

    public int Position { get; set; }

    public required string Type { get; set; }

    public required string Name { get; set; }

    /// <summary>Step configuration (jsonb), schema depends on <see cref="Type"/>.</summary>
    public string Config { get; set; } = "{}";
}

/// <summary>Daily LLM cost rollup per agent and model (Postgres <c>cost_daily</c>).</summary>
public sealed class CostDaily
{
    public DateOnly Day { get; set; }

    public required string AgentId { get; set; }

    public required string Model { get; set; }

    public long Calls { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public long CacheReadTokens { get; set; }

    public long CacheWriteTokens { get; set; }

    public decimal CostUsd { get; set; }
}

/// <summary>Workflow step types. New types only need a new executor.</summary>
public static class StepTypes
{
    public const string TriggerUserMessage = "trigger.user_message";
    public const string GuardInput = "guard.input";
    public const string KnowledgeSearch = "knowledge.search";
    public const string AgentLlm = "agent.llm";
    public const string SubagentCall = "subagent.call";
    public const string GuardOutput = "guard.output";
    public const string Respond = "respond";

    /// <summary>Child step types recorded under <see cref="AgentLlm"/>.</summary>
    public const string LlmCall = "llm.call";
    public const string ToolCall = "tool.call";
}

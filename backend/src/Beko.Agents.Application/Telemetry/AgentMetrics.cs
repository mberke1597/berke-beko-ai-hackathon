using System.Diagnostics.Metrics;

namespace Beko.Agents.Application.Telemetry;

/// <summary>
/// Agent run metrics. OTel names are translated to Prometheus as <c>gen_ai_client_token_usage</c>,
/// <c>gen_ai_cost_usd_total{agent,model}</c>, <c>agent_run_duration_seconds</c> and <c>tool_calls_total{tool,status}</c>.
/// </summary>
public static class AgentMetrics
{
    public static readonly Histogram<long> TokenUsage = BekoTelemetry.Meter.CreateHistogram<long>(
        "gen_ai.client.token.usage", unit: "{token}", description: "Tokens used per LLM call by token type.");

    public static readonly Histogram<double> OperationDuration = BekoTelemetry.Meter.CreateHistogram<double>(
        "gen_ai.client.operation.duration", unit: "s", description: "Duration of LLM calls.");

    public static readonly Counter<double> CostUsd = BekoTelemetry.Meter.CreateCounter<double>(
        "gen_ai.cost.usd", unit: "{USD}", description: "LLM cost in USD from PricingOptions.");

    public static readonly Histogram<double> RunDuration = BekoTelemetry.Meter.CreateHistogram<double>(
        "agent.run.duration", unit: "s", description: "Duration of agent runs.");

    public static readonly Counter<long> GuardrailBlocks = BekoTelemetry.Meter.CreateCounter<long>(
        "guardrail.blocks", unit: "{block}", description: "Messages blocked by a guardrail, by reason.");

    public static readonly Counter<long> ToolCalls = BekoTelemetry.Meter.CreateCounter<long>(
        "tool.calls", unit: "{call}", description: "Tool calls by tool and status.");
}

/// <summary>OpenTelemetry GenAI semantic convention attribute names and values used by the platform.</summary>
public static class GenAiAttributes
{
    public const string OperationName = "gen_ai.operation.name";
    public const string ProviderName = "gen_ai.provider.name";
    public const string System = "gen_ai.system";
    public const string RequestModel = "gen_ai.request.model";
    public const string RequestMaxTokens = "gen_ai.request.max_tokens";
    public const string ResponseModel = "gen_ai.response.model";
    public const string ResponseFinishReasons = "gen_ai.response.finish_reasons";
    public const string UsageInputTokens = "gen_ai.usage.input_tokens";
    public const string UsageOutputTokens = "gen_ai.usage.output_tokens";
    public const string UsageCacheReadInputTokens = "gen_ai.usage.cache_read.input_tokens";
    public const string UsageCacheCreationInputTokens = "gen_ai.usage.cache_creation.input_tokens";
    public const string TokenType = "gen_ai.token.type";
    public const string AgentId = "gen_ai.agent.id";
    public const string AgentName = "gen_ai.agent.name";
    public const string ConversationId = "gen_ai.conversation.id";
    public const string ToolName = "gen_ai.tool.name";
    public const string ToolCallId = "gen_ai.tool.call.id";
    public const string ToolType = "gen_ai.tool.type";

    public const string Anthropic = "anthropic";
    public const string InvokeAgent = "invoke_agent";
    public const string Chat = "chat";
    public const string ExecuteTool = "execute_tool";
}

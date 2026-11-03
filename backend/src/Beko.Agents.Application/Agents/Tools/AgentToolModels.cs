using System.Text.Json;
using Beko.Agents.Application.Agents.Runs;

namespace Beko.Agents.Application.Agents.Tools;

/// <summary>A tool the model can call. Implementations are found by <see cref="Name"/> (Postgres <c>tools.name</c>).</summary>
public interface IAgentTool
{
    string Name { get; }

    string Description { get; }

    /// <summary>JSON schema of the input object.</summary>
    JsonElement InputSchema { get; }

    Task<ToolExecutionResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken);
}

/// <summary>Who runs the tool: the conversation's member and the run.</summary>
public sealed record ToolContext(string RunId, string ConversationId, string AgentId, string MemberId);

/// <summary>
/// What the tool returns to the model (<see cref="Content"/>, JSON text) and, for device commands, the action shown
/// with the answer. <see cref="IsError"/> results go back to the model as <c>tool_result</c> errors.
/// </summary>
public sealed record ToolExecutionResult(string Content, bool IsError, AgentAction? Action = null, string? Summary = null);

public interface IToolRegistry
{
    IAgentTool? Find(string name);
}

public sealed class ToolRegistry(IEnumerable<IAgentTool> tools) : IToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public IAgentTool? Find(string name) => _tools.GetValueOrDefault(name);
}

/// <summary>Turns a <see cref="JsonElement"/> into plain values (bool, double, string, lists, dictionaries) that both JSON and BSON serialize.</summary>
public static class JsonValues
{
    public static object? ToPlain(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => element.TryGetInt64(out var integer) ? (object)integer : element.GetDouble(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Array => element.EnumerateArray().Select(ToPlain).ToList(),
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToPlain(p.Value), StringComparer.Ordinal),
            _ => null,
        };
}

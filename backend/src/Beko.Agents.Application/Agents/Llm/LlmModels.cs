using System.Text.Json;

namespace Beko.Agents.Application.Agents.Llm;

/// <summary>
/// One model call (one turn). The tool-use loop runs in the <c>agent.llm</c> step so every call and tool execution
/// is recorded as its own child step.
/// </summary>
public interface ILlmClient
{
    /// <summary>Calls the model, streaming text through <paramref name="onTextDelta"/>, and returns the full response.</summary>
    Task<LlmResponse> CompleteAsync(LlmRequest request, Func<string, Task>? onTextDelta, CancellationToken cancellationToken);
}

public sealed record LlmRequest(
    string Model,
    string System,
    IReadOnlyList<LlmMessage> Messages,
    IReadOnlyList<LlmToolDefinition> Tools,
    int MaxTokens);

public static class LlmRoles
{
    public const string User = "user";
    public const string Assistant = "assistant";
}

public sealed record LlmMessage(string Role, IReadOnlyList<LlmContent> Content)
{
    public static LlmMessage UserText(string text) => new(LlmRoles.User, [new LlmText(text)]);

    public static LlmMessage AssistantText(string text) => new(LlmRoles.Assistant, [new LlmText(text)]);
}

public abstract record LlmContent;

public sealed record LlmText(string Text) : LlmContent;

public sealed record LlmToolUse(string Id, string Name, JsonElement Input) : LlmContent;

public sealed record LlmToolResult(string ToolUseId, string Content, bool IsError) : LlmContent;

/// <summary>A tool as the model sees it. <see cref="InputSchema"/> is a JSON schema object.</summary>
public sealed record LlmToolDefinition(string Name, string Description, JsonElement InputSchema);

public sealed record LlmResponse(string Model, IReadOnlyList<LlmContent> Content, string StopReason, LlmUsage Usage)
{
    public string Text => string.Concat(Content.OfType<LlmText>().Select(t => t.Text));

    public IReadOnlyList<LlmToolUse> ToolUses => [.. Content.OfType<LlmToolUse>()];
}

public sealed record LlmUsage(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens)
{
    public static readonly LlmUsage Zero = new(0, 0, 0, 0);

    public static LlmUsage operator +(LlmUsage a, LlmUsage b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return new(a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens, a.CacheReadTokens + b.CacheReadTokens, a.CacheWriteTokens + b.CacheWriteTokens);
    }

    public LlmUsage Add(LlmUsage other) => this + other;
}

public static class StopReasons
{
    public const string EndTurn = "end_turn";
    public const string ToolUse = "tool_use";
    public const string MaxTokens = "max_tokens";
}

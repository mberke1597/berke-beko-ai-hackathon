using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Beko.Agents.Application.Agents.Llm;

namespace Beko.Agents.Infrastructure.Llm;

/// <summary>
/// <see cref="ILlmClient"/> on the official <c>Anthropic</c> SDK (v13, API version 2026-10-21). Streams the response,
/// forwards text deltas and rebuilds the final content (text and tool_use blocks) and usage from the stream events.
/// The SDK emits its own HTTP spans under <c>com.anthropic.sdk.csharp</c>.
/// </summary>
public sealed class AnthropicLlmClient(AnthropicClient client) : ILlmClient
{
    public async Task<LlmResponse> CompleteAsync(LlmRequest request, Func<string, Task>? onTextDelta, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parameters = BuildParams(request);
        var blocks = new SortedDictionary<long, BlockBuilder>();
        var model = request.Model;
        var stopReason = string.Empty;
        long inputTokens = 0, outputTokens = 0, cacheRead = 0, cacheWrite = 0;

        await foreach (var streamEvent in client.Messages.CreateStreaming(parameters, cancellationToken))
        {
            if (streamEvent.TryPickStart(out var start))
            {
                model = start.Message.Model.Raw() ?? model;
                inputTokens = start.Message.Usage.InputTokens;
                outputTokens = start.Message.Usage.OutputTokens;
                cacheRead = start.Message.Usage.CacheReadInputTokens ?? 0;
                cacheWrite = start.Message.Usage.CacheCreationInputTokens ?? 0;
            }
            else if (streamEvent.TryPickContentBlockStart(out var blockStart))
            {
                if (blockStart.ContentBlock.TryPickText(out var text))
                {
                    blocks[blockStart.Index] = new BlockBuilder(null, null, new StringBuilder(text.Text));
                }
                else if (blockStart.ContentBlock.TryPickToolUse(out var toolUse))
                {
                    blocks[blockStart.Index] = new BlockBuilder(toolUse.ID, toolUse.Name, new StringBuilder());
                }
            }
            else if (streamEvent.TryPickContentBlockDelta(out var blockDelta) && blocks.TryGetValue(blockDelta.Index, out var builder))
            {
                if (blockDelta.Delta.TryPickText(out var textDelta))
                {
                    builder.Buffer.Append(textDelta.Text);
                    if (onTextDelta is not null && textDelta.Text.Length > 0)
                    {
                        await onTextDelta(textDelta.Text);
                    }
                }
                else if (blockDelta.Delta.TryPickInputJson(out var jsonDelta))
                {
                    builder.Buffer.Append(jsonDelta.PartialJson);
                }
            }
            else if (streamEvent.TryPickDelta(out var messageDelta))
            {
                stopReason = messageDelta.Delta.StopReason?.Raw() ?? stopReason;
                outputTokens = messageDelta.Usage.OutputTokens;
                inputTokens = messageDelta.Usage.InputTokens ?? inputTokens;
                cacheRead = messageDelta.Usage.CacheReadInputTokens ?? cacheRead;
                cacheWrite = messageDelta.Usage.CacheCreationInputTokens ?? cacheWrite;
            }
        }

        var content = blocks.Values.Select(b => b.Build()).ToList();
        return new LlmResponse(model, content, stopReason, new LlmUsage(inputTokens, outputTokens, cacheRead, cacheWrite));
    }

    /// <summary>Maps the provider-neutral request to SDK parameters. Automatic prompt caching is on.</summary>
    public static MessageCreateParams BuildParams(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            System = request.System,
            CacheControl = new CacheControlEphemeral(),
            Messages = [.. request.Messages.Select(ToMessageParam)],
            Tools = [.. request.Tools.Select(ToTool)],
        };
    }

    private static MessageParam ToMessageParam(LlmMessage message)
    {
        List<ContentBlockParam> content = [.. message.Content.Select(ToContentBlock)];
        return message.Role == LlmRoles.Assistant
            ? new AssistantMessageParam { Content = content }
            : new UserMessageParam { Content = content };
    }

    private static ContentBlockParam ToContentBlock(LlmContent content) =>
        content switch
        {
            LlmText text => new TextBlockParam { Text = text.Text },
            LlmToolUse toolUse => new ToolUseBlockParam { ID = toolUse.Id, Name = toolUse.Name, Input = ToDictionary(toolUse.Input) },
            LlmToolResult result => new ToolResultBlockParam { ToolUseID = result.ToolUseId, Content = result.Content, IsError = result.IsError },
            _ => throw new NotSupportedException($"Unsupported content {content.GetType().Name}."),
        };

    private static ToolUnion ToTool(LlmToolDefinition tool)
    {
        var schema = tool.InputSchema;
        var properties = schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
            ? ToDictionary(props)
            : new Dictionary<string, JsonElement>();
        var required = schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array
            ? req.EnumerateArray().Select(r => r.GetString()!).ToList()
            : [];
        return new Tool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = new InputSchema { Properties = properties, Required = required },
        };
    }

    private static Dictionary<string, JsonElement> ToDictionary(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal)
            : [];

    private sealed record BlockBuilder(string? ToolUseId, string? ToolName, StringBuilder Buffer)
    {
        public LlmContent Build()
        {
            if (ToolUseId is null || ToolName is null)
            {
                return new LlmText(Buffer.ToString());
            }

            var json = Buffer.Length == 0 ? "{}" : Buffer.ToString();
            using var document = JsonDocument.Parse(json);
            return new LlmToolUse(ToolUseId, ToolName, document.RootElement.Clone());
        }
    }
}

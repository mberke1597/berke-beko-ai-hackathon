using Beko.Agents.Application.Agents.Runs;
using Microsoft.AspNetCore.SignalR;

namespace Beko.Agents.Api.Hubs;

/// <summary>Live run progress at <c>/hubs/runs</c>. Clients join one group per conversation.</summary>
public sealed class RunHub : Hub
{
    public const string Path = "/hubs/runs";

    public static string ConversationGroup(string conversationId) => $"conversation:{conversationId}";

    public Task JoinConversation(string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        return Groups.AddToGroupAsync(Context.ConnectionId, ConversationGroup(conversationId), Context.ConnectionAborted);
    }
}

/// <summary>Sends run events to the conversation's SignalR group, with the payloads from the contract.</summary>
internal sealed class SignalRRunNotifier(IHubContext<RunHub> hub) : IRunNotifier
{
    public Task RunStartedAsync(string conversationId, string runId, string agentId, string traceId, CancellationToken cancellationToken) =>
        Send(conversationId, "RunStarted", new { runId, conversationId, agentId, traceId }, cancellationToken);

    public Task StepStartedAsync(string conversationId, string runId, StepView step, CancellationToken cancellationToken) =>
        Send(conversationId, "StepStarted", new { runId, step }, cancellationToken);

    public Task StepCompletedAsync(string conversationId, string runId, StepView step, CancellationToken cancellationToken) =>
        Send(conversationId, "StepCompleted", new { runId, step }, cancellationToken);

    public Task StepFailedAsync(string conversationId, string runId, StepView step, string error, CancellationToken cancellationToken) =>
        Send(conversationId, "StepFailed", new { runId, step, error }, cancellationToken);

    public Task TextDeltaAsync(string conversationId, string runId, string delta, CancellationToken cancellationToken) =>
        Send(conversationId, "TextDelta", new { runId, delta }, cancellationToken);

    public Task RunCompletedAsync(string conversationId, string runId, string text, IReadOnlyList<AgentAction> actions, RunTotals totals, CancellationToken cancellationToken) =>
        Send(conversationId, "RunCompleted", new { runId, text, actions, totals }, cancellationToken);

    private Task Send(string conversationId, string method, object payload, CancellationToken cancellationToken) =>
        hub.Clients.Group(RunHub.ConversationGroup(conversationId)).SendAsync(method, payload, cancellationToken);
}

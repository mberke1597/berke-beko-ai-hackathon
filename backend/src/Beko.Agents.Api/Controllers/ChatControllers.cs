using System.ComponentModel.DataAnnotations;
using Beko.Agents.Application.Agents;
using Beko.Agents.Application.Agents.Runs;
using Microsoft.AspNetCore.Mvc;

namespace Beko.Agents.Api.Controllers;

[ApiController]
[Route("api/agents")]
public sealed class AgentsController(ChatService chat) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AgentSummary>>> List(CancellationToken cancellationToken) =>
        Ok(await chat.GetAgentsAsync(cancellationToken));

    [HttpGet("{id}")]
    public async Task<ActionResult<AgentDetails>> Get(string id, CancellationToken cancellationToken) =>
        await chat.GetAgentAsync(id, cancellationToken) is { } agent ? agent : NotFound();
}

[ApiController]
[Route("api/conversations")]
public sealed class ConversationsController(ChatService chat) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<ConversationCreatedResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateConversationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var conversation = await chat.CreateConversationAsync(request.AgentId, request.MemberId, cancellationToken);
        if (conversation is null)
        {
            return NotFound();
        }

        var response = new ConversationCreatedResponse(conversation.ConversationId, conversation.AgentId, conversation.CreatedAtUtc);
        return Created($"/api/conversations/{conversation.ConversationId}/messages", response);
    }

    [HttpGet("{id}/messages")]
    public async Task<ActionResult<IReadOnlyList<MessageResponse>>> GetMessages(string id, CancellationToken cancellationToken)
    {
        var messages = await chat.GetMessagesAsync(id, cancellationToken);
        return messages is null
            ? NotFound()
            : Ok(messages.Select(m => new MessageResponse(m.MessageId, m.Role, m.Text, m.RunId, m.CreatedAtUtc, m.Actions)).ToList());
    }

    /// <summary>Stores the message and starts a background run; progress streams over RunHub.</summary>
    [HttpPost("{id}/messages")]
    [ProducesResponseType<MessageAcceptedResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> PostMessage(string id, [FromBody] PostMessageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var outcome = await chat.PostMessageAsync(id, request.Text.Trim(), request.Channel ?? "text", cancellationToken);
        if (outcome.QueueFull)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Sistem şu anda yoğun, lütfen biraz sonra tekrar deneyin.");
        }

        return outcome.RunId is null ? NotFound() : Accepted(new MessageAcceptedResponse(outcome.RunId, outcome.TraceId!));
    }
}

[ApiController]
[Route("api/runs")]
public sealed class RunsController(ChatService chat) : ControllerBase
{
    /// <summary>The most recent runs, newest first, for the dashboard.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RunSummary>>> List([FromQuery] int limit = 50, CancellationToken cancellationToken = default) =>
        Ok(await chat.GetRecentRunsAsync(limit, cancellationToken));

    [HttpGet("{runId}")]
    public async Task<ActionResult<RunDetails>> Get(string runId, CancellationToken cancellationToken) =>
        await chat.GetRunAsync(runId, cancellationToken) is { } run ? run : NotFound();
}

public sealed record CreateConversationRequest([Required] string AgentId, string? MemberId);

public sealed record ConversationCreatedResponse(string ConversationId, string AgentId, DateTime CreatedAtUtc);

public sealed record PostMessageRequest([Required, StringLength(4000, MinimumLength = 1)] string Text, [AllowedValues("voice", "text", null)] string? Channel);

public sealed record MessageAcceptedResponse(string RunId, string TraceId);

public sealed record MessageResponse(string MessageId, string Role, string Text, string? RunId, DateTime CreatedAtUtc, IReadOnlyList<AgentAction> Actions);

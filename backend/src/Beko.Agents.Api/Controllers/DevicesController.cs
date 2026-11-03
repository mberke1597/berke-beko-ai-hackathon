using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Beko.Agents.Application.Homes;
using Beko.Agents.Domain.Homes;
using Microsoft.AspNetCore.Mvc;

namespace Beko.Agents.Api.Controllers;

[ApiController]
[Route("api/devices")]
public sealed class DevicesController(DeviceService devices) : ControllerBase
{
    [HttpGet("{id}")]
    public async Task<ActionResult<Appliance>> Get(string id, CancellationToken cancellationToken) =>
        await devices.GetDeviceAsync(id, cancellationToken) is { } appliance ? appliance : NotFound();

    [HttpGet("{id}/telemetry")]
    public async Task<ActionResult<IReadOnlyList<TelemetryPoint>>> GetTelemetry(
        string id,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        CancellationToken cancellationToken) =>
        await devices.GetTelemetryAsync(id, CommandProblems.AsUtc(fromUtc), CommandProblems.AsUtc(toUtc), cancellationToken) is { } telemetry
            ? Ok(telemetry)
            : NotFound();

    [HttpPost("{id}/commands")]
    [ProducesResponseType<CommandResult>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SendCommand(string id, [FromBody] DeviceCommandRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var command = new DeviceCommand(
            ApplianceCommand.Create(request.Type, request.Value),
            request.Source,
            string.IsNullOrWhiteSpace(request.MemberId) ? DemoHome.DefaultMemberId : request.MemberId);
        try
        {
            return await devices.SendCommandAsync(id, command, cancellationToken) is { } result ? Accepted(result) : NotFound();
        }
        catch (ApplianceCommandRejectedException ex)
        {
            return CommandProblems.Rejected(this, ex.Rejection);
        }
    }
}

/// <summary>Command from the app. <see cref="Source"/> tells where it came from; <see cref="MemberId"/> defaults to the default member.</summary>
public sealed record DeviceCommandRequest(
    [Required] string Type,
    JsonElement Value,
    [Required, AllowedValues("app", "remote", "voice", "ai", "schedule")] string Source,
    string? MemberId);

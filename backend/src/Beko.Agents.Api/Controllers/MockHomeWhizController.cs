using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Beko.Agents.Application.Homes;
using Beko.Agents.Domain.Homes;
using Beko.Agents.Infrastructure.HomeWhiz.Mock;
using Microsoft.AspNetCore.Mvc;

namespace Beko.Agents.Api.Controllers;

/// <summary>Mock of the external HomeWhiz API. State lives in memory and resets on restart.</summary>
[ApiController]
[Route("api/mock/homewhiz")]
public sealed class MockHomeWhizController(MockHomeWhizStore store) : ControllerBase
{
    [HttpGet("homes")]
    public ActionResult<IReadOnlyList<HomeSummary>> GetHomes() => Ok(MockHomeWhizStore.GetHomes());

    [HttpGet("homes/{homeId}")]
    public ActionResult<HomeDetails> GetHome(string homeId) =>
        MockHomeWhizStore.GetHome(homeId) is { } home ? home : NotFound();

    [HttpGet("homes/{homeId}/rooms")]
    public ActionResult<IReadOnlyList<RoomWithAppliances>> GetRooms(string homeId) =>
        store.GetRooms(homeId) is { } rooms ? Ok(rooms) : NotFound();

    [HttpGet("homes/{homeId}/appliances")]
    public ActionResult<IReadOnlyList<Appliance>> GetAppliances(string homeId) =>
        store.GetAppliances(homeId) is { } appliances ? Ok(appliances) : NotFound();

    [HttpGet("appliances/{id}")]
    public ActionResult<Appliance> GetAppliance(string id) =>
        store.GetAppliance(id) is { } appliance ? appliance : NotFound();

    [HttpGet("appliances/{id}/telemetry")]
    public ActionResult<IReadOnlyList<TelemetryPoint>> GetTelemetry(string id, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc) =>
        store.GetTelemetry(id, CommandProblems.AsUtc(fromUtc), CommandProblems.AsUtc(toUtc)) is { } telemetry ? Ok(telemetry) : NotFound();

    [HttpPost("appliances/{id}/commands")]
    [ProducesResponseType<CommandResult>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public IActionResult SendCommand(string id, [FromBody] MockCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var outcome = store.ExecuteCommand(id, ApplianceCommand.Create(request.Type, request.Value));
        return outcome switch
        {
            { Result: { } result } => Accepted(result),
            { Rejection: { } rejection } => CommandProblems.Rejected(this, rejection),
            _ => NotFound(),
        };
    }
}

public sealed record MockCommandRequest([Required] string Type, JsonElement Value);

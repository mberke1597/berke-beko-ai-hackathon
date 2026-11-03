using Beko.Agents.Application.Homes;
using Microsoft.AspNetCore.Mvc;

namespace Beko.Agents.Api.Controllers;

[ApiController]
[Route("api/homes")]
public sealed class HomesController(DeviceService devices) : ControllerBase
{
    /// <summary>The caller's home with rooms, members and all appliances. Until auth exists this is the demo home.</summary>
    [HttpGet("me")]
    public async Task<ActionResult<MyHome>> GetMine(CancellationToken cancellationToken) =>
        await devices.GetMyHomeAsync(cancellationToken) is { } home ? home : NotFound();
}

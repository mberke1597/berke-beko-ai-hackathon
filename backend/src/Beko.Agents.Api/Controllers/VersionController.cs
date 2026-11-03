using Beko.Agents.Application.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Beko.Agents.Api.Controllers;

[ApiController]
[Route("api/version")]
public sealed class VersionController(IOptions<AppOptions> appOptions, IHostEnvironment environment, TimeProvider timeProvider) : ControllerBase
{
    [HttpGet]
    public ActionResult<VersionResponse> Get()
    {
        var app = appOptions.Value;
        return new VersionResponse(
            AppOptions.ProductName,
            app.Version,
            app.Commit,
            environment.EnvironmentName,
            timeProvider.GetUtcNow().UtcDateTime);
    }
}

public sealed record VersionResponse(string Name, string Version, string Commit, string Environment, DateTime ServerTimeUtc);

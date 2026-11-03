using Beko.Agents.Domain.Homes;
using Microsoft.AspNetCore.Mvc;

namespace Beko.Agents.Api.Controllers;

internal static class CommandProblems
{
    /// <summary>422 problem+json for a rejected command; <c>code</c> is stable, <c>title</c> is Turkish.</summary>
    public static ObjectResult Rejected(ControllerBase controller, CommandRejection rejection)
    {
        var problem = controller.ProblemDetailsFactory.CreateProblemDetails(
            controller.HttpContext,
            StatusCodes.Status422UnprocessableEntity,
            rejection.Title,
            detail: rejection.Detail);
        problem.Extensions["code"] = rejection.Code;
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status422UnprocessableEntity };
    }

    /// <summary>Treats unspecified query times as UTC and converts local ones.</summary>
    public static DateTime? AsUtc(DateTime? value) =>
        value switch
        {
            null => null,
            { Kind: DateTimeKind.Utc } utc => utc,
            { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
            { } unspecified => DateTime.SpecifyKind(unspecified, DateTimeKind.Utc),
        };
}

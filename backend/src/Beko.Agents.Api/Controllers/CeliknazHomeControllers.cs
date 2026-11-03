using System.ComponentModel.DataAnnotations;
using Beko.Agents.Application.Comfort;
using Beko.Agents.Application.Common;
using Beko.Agents.Application.Dashboard;
using Beko.Agents.Application.Energy;
using Beko.Agents.Application.Homes;
using Beko.Agents.Application.Learning;
using Beko.Agents.Application.Notifications;
using Beko.Agents.Application.Presence;
using Beko.Agents.Application.Settings;
using Beko.Agents.Application.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace Beko.Agents.Api.Controllers;

/// <summary>Shared helpers: member id defaulting and Turkish problem responses.</summary>
public abstract class CeliknazControllerBase(HomeDirectory directory) : ControllerBase
{
    protected static string MemberOrDefault(string? memberId) => string.IsNullOrWhiteSpace(memberId) ? DemoHome.DefaultMemberId : memberId;

    protected async Task<bool> KnownMemberAsync(string memberId, CancellationToken cancellationToken) =>
        await directory.IsMemberAsync(memberId, cancellationToken);

    protected ObjectResult UnknownMember() => Problem(statusCode: StatusCodes.Status404NotFound, title: "Ev üyesi bulunamadı.");

    protected ObjectResult BadInput(string title) => Problem(statusCode: StatusCodes.Status400BadRequest, title: title);
}

public sealed record SettingsUpdateRequest(string? PriorityMode, string? NotificationLevel, QuietHours? QuietHours, string? Clothing, bool? LocationSharing);

[ApiController]
public sealed class SettingsController(SettingsService settings, HomeContextService context, HomeDirectory directory) : CeliknazControllerBase(directory)
{
    [HttpGet("api/me/settings")]
    public async Task<IActionResult> Get([FromQuery] string? memberId, CancellationToken cancellationToken)
    {
        var id = MemberOrDefault(memberId);
        return await KnownMemberAsync(id, cancellationToken) ? Ok(settings.GetView(id)) : UnknownMember();
    }

    [HttpPut("api/me/settings")]
    public async Task<IActionResult> Put([FromQuery] string? memberId, [FromBody] SettingsUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = MemberOrDefault(memberId);
        if (!await KnownMemberAsync(id, cancellationToken))
        {
            return UnknownMember();
        }

        var error = settings.Update(id, new MemberSettingsUpdate(request.PriorityMode, request.NotificationLevel, request.QuietHours, request.Clothing, request.LocationSharing));
        return error is null ? Ok(settings.GetView(id)) : BadInput("Ayarlar geçersiz: " + error);
    }

    [HttpGet("api/homes/me/context")]
    public async Task<HomeContext> GetContext(CancellationToken cancellationToken) => await context.GetAsync(cancellationToken);
}

public sealed record ComfortFeedbackRequest([Required] string ApplianceId, int Sensation, string? Clothing, string? CommandId);

[ApiController]
public sealed class ComfortController(ComfortService comfort, ComfortFeedbackService feedback, HomeDirectory directory) : CeliknazControllerBase(directory)
{
    [HttpGet("api/devices/{id}/comfort")]
    public async Task<IActionResult> Get(string id, [FromQuery] string? memberIds, CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? ids = string.IsNullOrWhiteSpace(memberIds)
            ? null
            : [.. memberIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        var outcome = await comfort.AssessAsync(id, ids, cancellationToken);
        return outcome switch
        {
            { Response: { } response } => Ok(response),
            { Problem: "not_air_conditioner" } => Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Bu cihaz bir klima değil."),
            { Problem: "unknown_member" } => UnknownMember(),
            _ => NotFound(),
        };
    }

    [HttpPost("api/me/comfort-feedback")]
    public async Task<IActionResult> PostFeedback([FromQuery] string? memberId, [FromBody] ComfortFeedbackRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var problem = await feedback.SubmitAsync(MemberOrDefault(memberId), request.ApplianceId, request.Sensation, request.Clothing, request.CommandId, cancellationToken);
        return problem switch
        {
            null => NoContent(),
            "invalid_sensation" => BadInput("Hissiyat -1, 0 veya 1 olmalı."),
            "invalid_clothing" => BadInput("Giysi light, normal veya heavy olmalı."),
            "unknown_member" => UnknownMember(),
            "not_air_conditioner" => Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Bu cihaz bir klima değil."),
            _ => NotFound(),
        };
    }
}

[ApiController]
public sealed class EnergyController(EnergyTracker energy, CheapestTimeService cheapest, HomeDirectory directory) : CeliknazControllerBase(directory)
{
    [HttpGet("api/homes/me/energy")]
    public EnergySummary Get() => energy.Summary();

    [HttpGet("api/homes/me/cheapest-time")]
    public async Task<IActionResult> GetCheapest([FromQuery, Required] string applianceId, [FromQuery] string? programId, [FromQuery] DateTime? deadlineUtc, CancellationToken cancellationToken)
    {
        try
        {
            return await cheapest.FindAsync(applianceId, programId, CommandProblems.AsUtc(deadlineUtc), cancellationToken) is { } result ? Ok(result) : NotFound();
        }
        catch (ArgumentException ex)
        {
            return BadInput(ex.Message);
        }
    }
}

public sealed record RouteRequest([Required, AllowedValues("at_home", "commute_out", "commute_back")] string Route);

public sealed record SpokenPresenceRequest([Required, AllowedValues("leaving", "arriving")] string State);

[ApiController]
public sealed class PresenceController(
    ILocationSource location,
    IMockLocationControl routes,
    PresenceService presence,
    HomeDirectory directory) : CeliknazControllerBase(directory)
{
    [HttpGet("api/mock/location")]
    public async Task<IActionResult> GetLocation([FromQuery] string? memberId, CancellationToken cancellationToken)
    {
        var id = MemberOrDefault(memberId);
        return await KnownMemberAsync(id, cancellationToken) ? Ok(await location.GetAsync(id, cancellationToken)) : UnknownMember();
    }

    [HttpPut("api/mock/location/{memberId}/route")]
    public async Task<IActionResult> SetRoute(string memberId, [FromBody] RouteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await KnownMemberAsync(memberId, cancellationToken))
        {
            return UnknownMember();
        }

        routes.SetRoute(memberId, request.Route);
        return NoContent();
    }

    [HttpGet("api/homes/me/presence")]
    public async Task<IReadOnlyList<PresenceEntry>> List(CancellationToken cancellationToken) => await presence.GetAllAsync(cancellationToken);

    [HttpPost("api/me/presence")]
    public async Task<IActionResult> Post([FromQuery] string? memberId, [FromBody] SpokenPresenceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = MemberOrDefault(memberId);
        if (!await KnownMemberAsync(id, cancellationToken))
        {
            return UnknownMember();
        }

        return await presence.SetSpokenAsync(id, request.State, cancellationToken) ? NoContent() : BadInput("Durum leaving veya arriving olmalı.");
    }
}

public sealed record CreateTaskRequest([Required, StringLength(200, MinimumLength = 1)] string Title, string? AssignedMemberId, int EffortPoints = 1);

public sealed record ReassignRequest(string? MemberId);

[ApiController]
public sealed class TasksController(TaskService tasks, HomeDirectory directory) : CeliknazControllerBase(directory)
{
    [HttpGet("api/homes/me/tasks")]
    public IReadOnlyList<HouseholdTask> List([FromQuery] string? status) => tasks.List(status);

    [HttpPost("api/homes/me/tasks")]
    public async Task<IActionResult> Create([FromBody] CreateTaskRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var task = await tasks.CreateAsync(request.Title, request.AssignedMemberId, Math.Clamp(request.EffortPoints, 0, 20), "manual", cancellationToken);
        return task is null ? UnknownMember() : Created($"/api/homes/me/tasks?status=open#{task.Id}", task);
    }

    [HttpPost("api/tasks/{id}/done")]
    public IActionResult Done(string id) => tasks.Complete(id, done: true) is { } task ? Ok(task) : NotFound();

    [HttpPost("api/tasks/{id}/skip")]
    public IActionResult Skip(string id) => tasks.Complete(id, done: false) is { } task ? Ok(task) : NotFound();

    [HttpPost("api/tasks/{id}/reassign")]
    public async Task<IActionResult> Reassign(string id, [FromBody] ReassignRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await tasks.ReassignAsync(id, request.MemberId, cancellationToken) is { } task ? Ok(task) : NotFound();
    }

    [HttpGet("api/homes/me/task-rules")]
    public IReadOnlyList<TaskRule> Rules() => tasks.Rules();

    [HttpPut("api/homes/me/task-rules/{id}")]
    public async Task<IActionResult> UpdateRule(string id, [FromBody] TaskRuleUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        var (rule, error) = await tasks.UpdateRuleAsync(id, update, cancellationToken);
        return rule is not null ? Ok(rule) : error == "not_found" ? NotFound() : BadInput("Kural geçersiz: " + error);
    }

    [HttpGet("api/homes/me/task-stats")]
    public async Task<IReadOnlyList<TaskStat>> Stats(CancellationToken cancellationToken) => await tasks.StatsAsync(cancellationToken);
}

public sealed record SeedHistoryRequest(string? MemberId, int Weeks = 5);

[ApiController]
public sealed class LearningController(LearningService learning, HistorySeeder seeder, IWebHostEnvironment environment, HomeDirectory directory) : CeliknazControllerBase(directory)
{
    [HttpGet("api/me/profile")]
    public async Task<IActionResult> Profile([FromQuery] string? memberId, CancellationToken cancellationToken) =>
        await learning.GetProfileAsync(MemberOrDefault(memberId), cancellationToken) is { } profile ? Ok(profile) : UnknownMember();

    [HttpPost("api/me/routines/{id}/accept")]
    public IActionResult Accept(string id) => learning.Accept(id) is { } routine ? Ok(routine) : NotFound();

    [HttpPost("api/me/routines/{id}/reject")]
    public IActionResult Reject(string id) => learning.Reject(id) is { } routine ? Ok(routine) : NotFound();

    [HttpDelete("api/me/learning")]
    public async Task<IActionResult> Reset([FromQuery] string? memberId, CancellationToken cancellationToken) =>
        await learning.ResetAsync(MemberOrDefault(memberId), cancellationToken) ? NoContent() : UnknownMember();

    /// <summary>Development only: synthetic history for Ayşe and Mehmet, then the learner runs at once.</summary>
    [HttpPost("api/demo/seed-history")]
    public async Task<IActionResult> SeedHistory([FromBody] SeedHistoryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }

        if (request.Weeks is < 1 or > 12)
        {
            return BadInput("Hafta sayısı 1 ile 12 arasında olmalı.");
        }

        if (!await KnownMemberAsync(MemberOrDefault(request.MemberId), cancellationToken))
        {
            return UnknownMember();
        }

        return Ok(new SeedHistoryResponse(await seeder.SeedAsync(request.Weeks, cancellationToken)));
    }
}

public sealed record SeedHistoryResponse(int Events);

[ApiController]
public sealed class NotificationsController(NotificationService notifications, HomeDirectory directory) : CeliknazControllerBase(directory)
{
    [HttpGet("api/me/notifications")]
    public async Task<IActionResult> List([FromQuery] string? memberId, CancellationToken cancellationToken)
    {
        var id = MemberOrDefault(memberId);
        return await KnownMemberAsync(id, cancellationToken) ? Ok(notifications.List(id)) : UnknownMember();
    }

    [HttpPost("api/me/notifications/{id}/read")]
    public IActionResult Read(string id) => notifications.MarkRead(id) is { } notification ? Ok(notification) : NotFound();
}

[ApiController]
[Route("api/dashboard")]
public sealed class DashboardController(DashboardService dashboard) : ControllerBase
{
    [HttpGet("summary")]
    public async Task<DashboardSummary> Summary(CancellationToken cancellationToken) => await dashboard.GetSummaryAsync(cancellationToken);

    [HttpGet("projection")]
    public async Task<IActionResult> Projection([FromQuery, Range(1, 100_000_000)] int homes = 100_000, CancellationToken cancellationToken = default) =>
        Ok(await dashboard.GetProjectionAsync(homes, cancellationToken));
}

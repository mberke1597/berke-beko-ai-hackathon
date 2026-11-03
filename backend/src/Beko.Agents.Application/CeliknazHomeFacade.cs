using Beko.Agents.Application.Comfort;
using Beko.Agents.Application.Common;
using Beko.Agents.Application.Energy;
using Beko.Agents.Application.Homes;
using Beko.Agents.Application.Learning;
using Beko.Agents.Application.Presence;
using Beko.Agents.Application.Settings;
using Beko.Agents.Application.Tasks;

namespace Beko.Agents.Application;

/// <summary>Result of an AI-requested change that can be refused with a reason the agent can relay.</summary>
public sealed record FacadeResult<T>(T? Value, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>Creates <see cref="FacadeResult{T}"/> values.</summary>
public static class Facade
{
    public static FacadeResult<T> Success<T>(T value) => new(value, null);

    public static FacadeResult<T> Fail<T>(string error) => new(default, error);
}

/// <summary>Rough energy estimate for a plan: an air conditioner running for some hours, or one program run.</summary>
public sealed record EnergyEstimate(double Kwh, double CostTl, string Basis);

/// <summary>
/// One entry point for the slice 2 agent tools (<c>get_comfort_assessment</c>, <c>report_thermal_sensation</c>, <c>get_member_profile</c>,
/// <c>set_presence</c>, <c>get_settings</c>, <c>update_settings</c>, <c>find_cheapest_time</c>, <c>estimate_energy</c>, <c>list_tasks</c>,
/// <c>create_task</c>, <c>assign_task</c>, <c>propose_automation</c>). Every method returns plain data or a short English error the tool can relay.
/// </summary>
public interface ICeliknazHomeFacade
{
    Task<FacadeResult<ComfortResponse>> GetComfortAssessmentAsync(string applianceId, IReadOnlyList<string>? memberIds, CancellationToken cancellationToken);

    /// <remarks>-1 cold, 0 fine, 1 hot.</remarks>
    Task<FacadeResult<bool>> ReportThermalSensationAsync(string memberId, string applianceId, int sensation, string? clothing, string? commandId, CancellationToken cancellationToken);

    Task<FacadeResult<MemberProfileView>> GetMemberProfileAsync(string memberId, CancellationToken cancellationToken);

    /// <remarks><c>leaving</c> or <c>arriving</c>.</remarks>
    Task<FacadeResult<IReadOnlyList<PresenceEntry>>> SetPresenceAsync(string memberId, string state, CancellationToken cancellationToken);

    Task<FacadeResult<MemberSettingsView>> GetSettingsAsync(string memberId, CancellationToken cancellationToken);

    Task<FacadeResult<MemberSettingsView>> UpdateSettingsAsync(string memberId, MemberSettingsUpdate update, CancellationToken cancellationToken);

    Task<FacadeResult<CheapestTime>> FindCheapestTimeAsync(string applianceId, string? programId, DateTime? deadlineUtc, CancellationToken cancellationToken);

    /// <summary>Air conditioner: kWh and TL for <paramref name="hours"/> at the given mode and setpoint (defaults: current). Washer or dishwasher: one run of <paramref name="programId"/> (default: selected).</summary>
    Task<FacadeResult<EnergyEstimate>> EstimateEnergyAsync(string applianceId, string? mode, double? setpoint, double hours, string? programId, CancellationToken cancellationToken);

    Task<IReadOnlyList<HouseholdTask>> ListTasksAsync(string? status, CancellationToken cancellationToken);

    /// <remarks><c>null</c> distributes by fair share.</remarks>
    Task<FacadeResult<HouseholdTask>> CreateTaskAsync(string title, string? assignedMemberId, int effortPoints, CancellationToken cancellationToken);

    /// <remarks><c>null</c> redistributes.</remarks>
    Task<FacadeResult<HouseholdTask>> AssignTaskAsync(string taskId, string? memberId, CancellationToken cancellationToken);

    /// <summary>Stores a routine suggestion and sends the member a notification with an accept action.</summary>
    Task<FacadeResult<Routine>> ProposeAutomationAsync(string memberId, string applianceId, string dayType, string time, string reason, CancellationToken cancellationToken);
}

/// <summary>Default <see cref="ICeliknazHomeFacade"/> over the slice 2 services.</summary>
public sealed class CeliknazHomeFacade(
    ComfortService comfort,
    ComfortFeedbackService feedback,
    LearningService learning,
    PresenceService presence,
    SettingsService settings,
    HomeDirectory directory,
    CheapestTimeService cheapest,
    EnergyModel energy,
    IHomeWhizClient homeWhiz,
    IHomeClock clock,
    TaskService tasks) : ICeliknazHomeFacade
{
    public async Task<FacadeResult<ComfortResponse>> GetComfortAssessmentAsync(string applianceId, IReadOnlyList<string>? memberIds, CancellationToken cancellationToken)
    {
        var outcome = await comfort.AssessAsync(applianceId, memberIds, cancellationToken);
        return outcome.Response is { } response ? Facade.Success(response) : Facade.Fail<ComfortResponse>(Describe(outcome.Problem));
    }

    public async Task<FacadeResult<bool>> ReportThermalSensationAsync(string memberId, string applianceId, int sensation, string? clothing, string? commandId, CancellationToken cancellationToken)
    {
        var problem = await feedback.SubmitAsync(memberId, applianceId, sensation, clothing, commandId, cancellationToken);
        return problem is null ? Facade.Success(true) : Facade.Fail<bool>(Describe(problem));
    }

    public async Task<FacadeResult<MemberProfileView>> GetMemberProfileAsync(string memberId, CancellationToken cancellationToken) =>
        await learning.GetProfileAsync(memberId, cancellationToken) is { } profile
            ? Facade.Success(profile)
            : Facade.Fail<MemberProfileView>("Unknown member.");

    public async Task<FacadeResult<IReadOnlyList<PresenceEntry>>> SetPresenceAsync(string memberId, string state, CancellationToken cancellationToken)
    {
        if (!await directory.IsMemberAsync(memberId, cancellationToken))
        {
            return Facade.Fail<IReadOnlyList<PresenceEntry>>("Unknown member.");
        }

        return await presence.SetSpokenAsync(memberId, state, cancellationToken)
            ? Facade.Success(await presence.GetAllAsync(cancellationToken))
            : Facade.Fail<IReadOnlyList<PresenceEntry>>("state must be leaving or arriving.");
    }

    public async Task<FacadeResult<MemberSettingsView>> GetSettingsAsync(string memberId, CancellationToken cancellationToken) =>
        await directory.IsMemberAsync(memberId, cancellationToken)
            ? Facade.Success(settings.GetView(memberId))
            : Facade.Fail<MemberSettingsView>("Unknown member.");

    public async Task<FacadeResult<MemberSettingsView>> UpdateSettingsAsync(string memberId, MemberSettingsUpdate update, CancellationToken cancellationToken)
    {
        if (!await directory.IsMemberAsync(memberId, cancellationToken))
        {
            return Facade.Fail<MemberSettingsView>("Unknown member.");
        }

        return settings.Update(memberId, update) is { } error
            ? Facade.Fail<MemberSettingsView>(error)
            : Facade.Success(settings.GetView(memberId));
    }

    public async Task<FacadeResult<CheapestTime>> FindCheapestTimeAsync(string applianceId, string? programId, DateTime? deadlineUtc, CancellationToken cancellationToken)
    {
        try
        {
            return await cheapest.FindAsync(applianceId, programId, deadlineUtc, cancellationToken) is { } result
                ? Facade.Success(result)
                : Facade.Fail<CheapestTime>("Unknown appliance.");
        }
        catch (ArgumentException ex)
        {
            return Facade.Fail<CheapestTime>(ex.Message);
        }
    }

    public async Task<FacadeResult<EnergyEstimate>> EstimateEnergyAsync(string applianceId, string? mode, double? setpoint, double hours, string? programId, CancellationToken cancellationToken)
    {
        var appliance = await homeWhiz.GetApplianceAsync(applianceId, cancellationToken);
        if (appliance is null)
        {
            return Facade.Fail<EnergyEstimate>("Unknown appliance.");
        }

        var local = clock.LocalNow();
        if (appliance.State.Ac is { } ac)
        {
            var useHours = Math.Clamp(hours, 0.1, 24);
            var kwh = energy.AcKw(mode ?? ac.Mode, setpoint ?? ac.Setpoint) * useHours;
            return Facade.Success(new EnergyEstimate(Math.Round(kwh, 2), Math.Round(energy.CostOf(local, TimeSpan.FromHours(useHours), kwh), 2), "ac_hours"));
        }

        var program = programId ?? appliance.State.Program?.Program;
        if (program is not null && energy.FindProgram(appliance.Type, program) is { } profile)
        {
            return Facade.Success(new EnergyEstimate(
                Math.Round(profile.Kwh, 2),
                Math.Round(energy.CostOf(local, TimeSpan.FromMinutes(profile.DurationMin), profile.Kwh), 2),
                "program_run"));
        }

        return Facade.Fail<EnergyEstimate>("No energy model for this appliance; pass an air conditioner or a program.");
    }

    public Task<IReadOnlyList<HouseholdTask>> ListTasksAsync(string? status, CancellationToken cancellationToken) =>
        Task.FromResult(tasks.List(status));

    public async Task<FacadeResult<HouseholdTask>> CreateTaskAsync(string title, string? assignedMemberId, int effortPoints, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Facade.Fail<HouseholdTask>("title is required.");
        }

        return await tasks.CreateAsync(title, assignedMemberId, Math.Clamp(effortPoints, 0, 20), "voice", cancellationToken) is { } task
            ? Facade.Success(task)
            : Facade.Fail<HouseholdTask>("Unknown member.");
    }

    public async Task<FacadeResult<HouseholdTask>> AssignTaskAsync(string taskId, string? memberId, CancellationToken cancellationToken) =>
        await tasks.ReassignAsync(taskId, memberId, cancellationToken) is { } task
            ? Facade.Success(task)
            : Facade.Fail<HouseholdTask>("Unknown task or member.");

    public async Task<FacadeResult<Routine>> ProposeAutomationAsync(string memberId, string applianceId, string dayType, string time, string reason, CancellationToken cancellationToken)
    {
        if (await homeWhiz.GetApplianceAsync(applianceId, cancellationToken) is null)
        {
            return Facade.Fail<Routine>("Unknown appliance.");
        }

        return await learning.ProposeAsync(memberId, applianceId, dayType, time, reason, cancellationToken) is { } routine
            ? Facade.Success(routine)
            : Facade.Fail<Routine>("Unknown member, dayType must be weekday or weekend, time must be HH:mm.");
    }

    private static string Describe(string? problem) =>
        problem switch
        {
            "not_found" => "Unknown appliance.",
            "not_air_conditioner" => "This appliance is not an air conditioner.",
            "unknown_member" => "Unknown member.",
            "invalid_sensation" => "sensation must be -1, 0 or 1.",
            "invalid_clothing" => "clothing must be light, normal or heavy.",
            _ => "Request failed.",
        };
}

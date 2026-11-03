using System.Globalization;
using System.Text.Json;
using Beko.Agents.Application.Common;
using Beko.Agents.Application.Notifications;
using Beko.Agents.Domain.Homes;

namespace Beko.Agents.Application.Learning;

/// <summary>Profile, routine decisions, reset and manual routine proposals.</summary>
public sealed class LearningService(
    MemberProfileStore profiles,
    HomeDirectory directory,
    NotificationService notifications,
    TimeProvider timeProvider)
{
    /// <summary>The member's learned profile, or <c>null</c> for an unknown member.</summary>
    public async Task<MemberProfileView?> GetProfileAsync(string memberId, CancellationToken cancellationToken) =>
        await directory.IsMemberAsync(memberId, cancellationToken) ? profiles.GetView(memberId) : null;

    /// <summary>Accepting a routine turns it into a schedule (see <see cref="RoutineScheduler"/>).</summary>
    public Routine? Accept(string routineId) => Decide(routineId, RoutineStatuses.Accepted);

    public Routine? Reject(string routineId) => Decide(routineId, RoutineStatuses.Rejected);

    /// <summary>Resets profile, routines and offset of the member; older history is ignored from now on.</summary>
    public async Task<bool> ResetAsync(string memberId, CancellationToken cancellationToken)
    {
        if (!await directory.IsMemberAsync(memberId, cancellationToken))
        {
            return false;
        }

        profiles.Reset(memberId, timeProvider.GetUtcNow().UtcDateTime);
        return true;
    }

    /// <summary>
    /// Proposes a routine (what the <c>propose_automation</c> tool does): stores it as <c>suggested</c> and sends a suggestion
    /// notification with an accept action. Returns <c>null</c> for an unknown member or appliance, or invalid time.
    /// </summary>
    public async Task<Routine?> ProposeAsync(
        string memberId,
        string applianceId,
        string dayType,
        string time,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!await directory.IsMemberAsync(memberId, cancellationToken)
            || dayType is not (HomeTime.Weekday or HomeTime.Weekend)
            || !TimeSpan.TryParseExact(time, @"hh\:mm", CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        var routine = new Routine(
            $"rt-{memberId}-{applianceId}-{dayType}",
            "power_on",
            applianceId,
            dayType,
            time,
            20,
            0.5,
            reason,
            RoutineStatuses.Suggested)
        {
            MemberId = memberId,
        };
        profiles.UpsertRoutine(routine);
        await notifications.CreateAsync(
            memberId,
            NotificationKinds.Suggestion,
            NotificationLevels.Important,
            "Otomasyon önerisi",
            $"{(dayType == HomeTime.Weekend ? "Hafta sonu" : "Hafta içi")} {time} civarında {applianceId} açılsın mı? {reason}",
            new NotificationAction("accept_routine", routine.Id),
            cancellationToken);
        return routine;
    }

    private Routine? Decide(string routineId, string status)
    {
        if (profiles.FindRoutine(routineId) is not { } routine)
        {
            return null;
        }

        var updated = routine with { Status = status };
        profiles.UpsertRoutine(updated);
        return updated;
    }
}

/// <summary>Runs accepted routines at their time on matching days (simulated home-local time) with <c>source: "schedule"</c>.</summary>
public sealed class RoutineScheduler(MemberProfileStore profiles, IAiCommandSender sender, IHomeClock clock, NotificationService notifications)
{
    private const double LateStartLimitMin = 60;

    private readonly Dictionary<string, DateTime> _lastFiredDay = new(StringComparer.Ordinal);

    public async Task ProcessDueAsync(CancellationToken cancellationToken)
    {
        var local = clock.LocalNow();
        var dayType = HomeTime.DayType(local);
        var minute = HomeTime.MinuteOfDay(local);
        foreach (var routine in profiles.AllRoutines().Where(r => r.Status == RoutineStatuses.Accepted && r.DayType == dayType && r.Action == "power_on"))
        {
            var due = TimeSpan.ParseExact(routine.Time, @"hh\:mm", CultureInfo.InvariantCulture).TotalMinutes;
            if (minute < due || minute > due + LateStartLimitMin || _lastFiredDay.GetValueOrDefault(routine.Id) == local.Date)
            {
                continue;
            }

            _lastFiredDay[routine.Id] = local.Date;
            var outcome = await sender.SendAsync(
                routine.ApplianceId,
                ApplianceCommand.Create(CommandTypes.Power, JsonSerializer.SerializeToElement(true)),
                routine.MemberId,
                cancellationToken,
                "schedule");
            if (outcome.Accepted)
            {
                await notifications.CreateAsync(
                    routine.MemberId,
                    NotificationKinds.Info,
                    NotificationLevels.Info,
                    "Rutin çalıştı",
                    $"{routine.ApplianceId} planlandığı gibi {routine.Time} civarında açıldı.",
                    null,
                    cancellationToken);
            }
        }
    }
}

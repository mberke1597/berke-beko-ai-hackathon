using System.Globalization;
using System.Text.Json;
using Beko.Agents.Application.Common;
using Beko.Agents.Application.Comfort;
using Beko.Agents.Application.Homes;
using Beko.Agents.Application.Learning;
using Beko.Agents.Application.Notifications;
using Beko.Agents.Application.Options;
using Beko.Agents.Application.Settings;
using Beko.Agents.Domain.Homes;
using Microsoft.Extensions.Options;

namespace Beko.Agents.Application.Presence;

/// <summary>Geofence with hysteresis: home below the home radius, near below the near radius, away beyond.</summary>
public static class Geofence
{
    public static string NextBand(string current, double distanceM, PresenceOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        var h = o.HysteresisM;
        return current switch
        {
            PresenceBands.Home => distanceM <= o.HomeRadiusM + h ? PresenceBands.Home : distanceM <= o.NearRadiusM + h ? PresenceBands.Near : PresenceBands.Away,
            PresenceBands.Near => distanceM < o.HomeRadiusM - h ? PresenceBands.Home : distanceM > o.NearRadiusM + h ? PresenceBands.Away : PresenceBands.Near,
            _ => distanceM < o.HomeRadiusM - h ? PresenceBands.Home : distanceM < o.NearRadiusM - h ? PresenceBands.Near : PresenceBands.Away,
        };
    }
}

/// <summary>
/// Polls the location source, keeps the distance band per member and acts on the home's presence: when nobody is home it
/// sets the air conditioners back by the default member's priority mode, when someone approaches it pre-conditions
/// (balanced and comfort; saving waits for the arrival). Every automatic action is a <c>source: "ai"</c> command and a notification.
/// </summary>
public sealed class PresenceService(
    ILocationSource location,
    IMockLocationControl routes,
    PresenceStore store,
    HomeDirectory directory,
    SettingsService settings,
    IHomeClock clock,
    IOptions<PresenceOptions> options,
    IHomeWhizClient homeWhiz,
    IAiCommandSender sender,
    ComfortService comfort,
    NotificationService notifications,
    MemberProfileStore profiles) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (double DistanceM, DateTime AtUtc)> _last = new(StringComparer.Ordinal);
    private readonly HashSet<string> _preConditioned = new(StringComparer.Ordinal);
    private bool? _anyoneHome;

    public void Dispose() => _gate.Dispose();

    public async Task<IReadOnlyList<PresenceEntry>> GetAllAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        return [.. (await directory.MemberIdsAsync(cancellationToken)).Select(id => store.Get(id, now))];
    }

    /// <summary>Reads every member's location, updates the bands and runs the automatic actions.</summary>
    public async Task PollAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var home = await directory.GetAsync(cancellationToken);
            foreach (var member in home.Members.Where(m => settings.Get(m.Id).LocationSharing))
            {
                var loc = await location.GetAsync(member.Id, cancellationToken);
                var distance = Geo.DistanceM(home.Location.Lat, home.Location.Lng, loc.Lat, loc.Lng);
                Update(member.Id, distance, loc.TimestampUtc);
            }

            await EvaluateAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Spoken trigger ("evden çıkıyorum" / "eve geliyorum"). Moves the mock route so the location source agrees, applies the band
    /// at once and evaluates. Returns <c>false</c> for an unknown state.
    /// </summary>
    public async Task<bool> SetSpokenAsync(string memberId, string state, CancellationToken cancellationToken)
    {
        if (state is not ("leaving" or "arriving"))
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var o = options.Value;
            if (state == "leaving")
            {
                routes.SetRoute(memberId, LocationRoutes.CommuteOut, o.NearRadiusM + 500);
                Update(memberId, o.NearRadiusM + 500, clock.UtcNow);
            }
            else
            {
                routes.SetRoute(memberId, LocationRoutes.CommuteBack, o.FarDistanceM);
                Update(memberId, o.FarDistanceM, clock.UtcNow);
            }

            await EvaluateAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Update(string memberId, double distanceM, DateTime atUtc)
    {
        var previous = store.Get(memberId, atUtc);
        var band = Geofence.NextBand(previous.Band, distanceM, options.Value);
        double? eta = null;
        if (_last.TryGetValue(memberId, out var last) && atUtc > last.AtUtc)
        {
            var speed = (last.DistanceM - distanceM) / (atUtc - last.AtUtc).TotalSeconds;
            if (speed > 0.5 && band != PresenceBands.Home)
            {
                eta = Math.Round(distanceM / speed / 60, 1);
            }
        }

        _last[memberId] = (distanceM, atUtc);
        store.Set(new PresenceEntry(memberId, band, Math.Round(distanceM), eta, atUtc));
    }

    private async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var ids = await directory.MemberIdsAsync(cancellationToken);
        var entries = ids.Select(id => store.Get(id, now)).ToList();
        var anyoneHome = entries.Any(e => e.Band == PresenceBands.Home);
        var mode = settings.Get(DemoHome.DefaultMemberId).PriorityMode;

        if (_anyoneHome == true && !anyoneHome)
        {
            _preConditioned.Clear();
            await SetBackAsync(mode, cancellationToken);
        }
        else if (_anyoneHome == false && anyoneHome)
        {
            _preConditioned.Clear();
            if (mode == PriorityModes.Saving)
            {
                await PreConditionAsync(entries.First(e => e.Band == PresenceBands.Home).MemberId, null, cancellationToken);
            }
        }

        _anyoneHome = anyoneHome;

        if (anyoneHome || mode == PriorityModes.Saving)
        {
            return;
        }

        var leadMin = mode == PriorityModes.Comfort ? options.Value.PreConditionMinutesComfort : options.Value.PreConditionMinutesBalanced;
        foreach (var entry in entries.Where(e => e.Band != PresenceBands.Home && !_preConditioned.Contains(e.MemberId)))
        {
            if (entry.EtaMin is { } eta && eta <= leadMin || LearnedReturnIsNear(entry.MemberId, leadMin))
            {
                _preConditioned.Add(entry.MemberId);
                await PreConditionAsync(entry.MemberId, entry.EtaMin, cancellationToken);
            }
        }
    }

    private bool LearnedReturnIsNear(string memberId, double leadMin)
    {
        var local = clock.LocalNow();
        var dayType = HomeTime.DayType(local);
        var minute = HomeTime.MinuteOfDay(local);
        return profiles.GetRoutines(memberId)
            .Where(r => r.Status == RoutineStatuses.Accepted && r.Action == "power_on" && r.DayType == dayType)
            .Any(r =>
            {
                var routineMinute = TimeSpan.ParseExact(r.Time, @"hh\:mm", CultureInfo.InvariantCulture).TotalMinutes;
                var ahead = (routineMinute - minute + 1440) % 1440;
                return ahead <= leadMin;
            });
    }

    private async Task SetBackAsync(string mode, CancellationToken cancellationToken)
    {
        var running = (await homeWhiz.GetAppliancesAsync(DemoHome.HomeId, cancellationToken) ?? [])
            .Where(a => a.State.Ac is not null && a.State.Power)
            .ToList();
        if (running.Count == 0)
        {
            return;
        }

        var changed = new List<string>();
        foreach (var ac in running)
        {
            var state = ac.State.Ac!;
            var ok = (mode, state.Mode) switch
            {
                (PriorityModes.Saving, _) => await SendAsync(ac.Id, "power", false, cancellationToken),
                (PriorityModes.Balanced, "cool") => await SendAsync(ac.Id, "setpoint", Math.Max(state.Setpoint, 28), cancellationToken),
                (PriorityModes.Balanced, "heat") => await SendAsync(ac.Id, "setpoint", Math.Min(state.Setpoint, 18), cancellationToken),
                (PriorityModes.Comfort, "cool") => await SendAsync(ac.Id, "setpoint", Math.Min(28, state.Setpoint + 2), cancellationToken),
                (PriorityModes.Comfort, "heat") => await SendAsync(ac.Id, "setpoint", Math.Max(18, state.Setpoint - 2), cancellationToken),
                _ => await SendAsync(ac.Id, "power", false, cancellationToken),
            };
            if (ok)
            {
                changed.Add(ac.DisplayName);
            }
        }

        if (changed.Count > 0)
        {
            var what = mode switch
            {
                PriorityModes.Saving => "kapattım",
                PriorityModes.Balanced => "ekonomik ayara aldım",
                _ => "hafifçe kıstım",
            };
            await NotifyAsync(
                "Evde kimse kalmadı",
                $"{string.Join(", ", changed)} {what}. İstediğinizde geri alabilirsiniz.",
                cancellationToken);
        }
    }

    private async Task PreConditionAsync(string memberId, double? etaMin, CancellationToken cancellationToken)
    {
        var acs = (await homeWhiz.GetAppliancesAsync(DemoHome.HomeId, cancellationToken) ?? []).Where(a => a.State.Ac is not null).ToList();
        var target = acs.FirstOrDefault(a => a.Id == "ac-salon") ?? acs.FirstOrDefault();
        if (target is null)
        {
            return;
        }

        var outcome = await comfort.AssessAsync(target.Id, [memberId], cancellationToken);
        if (outcome.Response?.Recommendation is not { } rec || rec.Action is ComfortActions.None or ComfortActions.Window)
        {
            return;
        }

        var ok = rec.Action switch
        {
            ComfortActions.Setpoint => await SendAsync(target.Id, "power", true, cancellationToken)
                && await SendAsync(target.Id, "mode", rec.Mode!, cancellationToken)
                && await SendAsync(target.Id, "setpoint", rec.Setpoint!.Value, cancellationToken),
            _ => await SendAsync(target.Id, "power", true, cancellationToken)
                && await SendAsync(target.Id, "mode", rec.Mode!, cancellationToken),
        };
        if (!ok)
        {
            return;
        }

        var name = (await directory.GetAsync(cancellationToken)).Members.FirstOrDefault(m => m.Id == memberId)?.DisplayName ?? memberId;
        var when = etaMin is { } e ? $"{name} yaklaşık {e.ToString("0", CultureInfo.InvariantCulture)} dakikada eve gelecek" : $"{name} eve geldi";
        await NotifyAsync(
            "Ev önceden hazırlandı",
            $"{when}; {target.DisplayName} için {rec.Reason}",
            cancellationToken);
    }

    private Task<Notification?> NotifyAsync(string title, string text, CancellationToken cancellationToken) =>
        notifications.CreateAsync(DemoHome.DefaultMemberId, NotificationKinds.Info, NotificationLevels.Important, title, text, null, cancellationToken);

    private async Task<bool> SendAsync(string applianceId, string type, object value, CancellationToken cancellationToken) =>
        (await sender.SendAsync(
            applianceId,
            ApplianceCommand.Create(type, JsonSerializer.SerializeToElement(value)),
            DemoHome.DefaultMemberId,
            cancellationToken)).Accepted;
}

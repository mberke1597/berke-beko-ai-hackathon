using System.Globalization;
using Beko.Agents.Application.Common;
using Beko.Agents.Application.Comfort;
using Beko.Agents.Application.Homes;
using Beko.Agents.Application.Notifications;

namespace Beko.Agents.Application.Learning;

/// <summary>
/// Finds patterns in <c>device_events</c> and <c>comfort_feedback</c> (no ML, everything explainable):
/// preferred setpoint per context (time-weighted median, 14-day half-life), routines (recurring within +-20 min on a
/// circular 24 h clock), overrides (an AI command followed by a user command within 30 simulated minutes, bias += 0.3 x delta),
/// sensitivity (feedback versus predicted PMV) and the weekly override and good-feedback rates.
/// Every run replays the history since the member's last reset, so it is idempotent.
/// </summary>
public sealed class PatternLearner(
    IDeviceEventReader events,
    IComfortFeedbackStore feedbackStore,
    MemberProfileStore profiles,
    HomeDirectory directory,
    NotificationService notifications,
    IHomeClock clock,
    TimeProvider timeProvider)
{
    public const int HistoryDays = 42;

    public const int RoutineWindowDays = 21;

    public const int RoutineWindowMin = 20;

    public const double OverrideWindowSimMinutes = 30;

    public const double HalfLifeDays = 14;

    public const double SuggestMinConfidence = 0.7;

    private static readonly string[] UserSources = ["app", "remote", "voice"];

    public async Task RunAllAsync(CancellationToken cancellationToken)
    {
        foreach (var memberId in await directory.MemberIdsAsync(cancellationToken))
        {
            await RunAsync(memberId, cancellationToken);
        }
    }

    public async Task RunAsync(string memberId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var since = now.AddDays(-HistoryDays);
        if (profiles.GetResetAtUtc(memberId) is { } reset && reset > since)
        {
            since = reset;
        }

        var all = await events.GetAsync(DemoHome.HomeId, since, cancellationToken);
        var feedback = (await feedbackStore.GetAsync(DemoHome.HomeId, since, cancellationToken))
            .Where(f => f.MemberId == memberId)
            .OrderBy(f => f.AtUtc)
            .ToList();

        var overrides = FindOverrides(all.OrderBy(e => e.AtUtc).ToList(), memberId);
        var (offset, bias) = Replay(feedback, overrides);
        var learned = new LearnedProfile(
            offset,
            bias,
            Preferences(all, memberId, now),
            Routines(all, memberId, now, since),
            WeeklyOverrideRate(all, overrides, memberId),
            WeeklyGoodFeedback(feedback));
        profiles.Apply(memberId, learned);

        await SuggestRoutinesAsync(memberId, cancellationToken);
    }

    private List<OverrideCase> FindOverrides(List<DeviceEventView> sorted, string memberId)
    {
        var result = new List<OverrideCase>();
        foreach (var byDevice in sorted.GroupBy(e => e.DeviceId))
        {
            var list = byDevice.ToList();
            for (var i = 0; i < list.Count - 1; i++)
            {
                var ai = list[i];
                var next = list[i + 1];
                if (ai.Source != "ai" || ai.MemberId != memberId || !UserSources.Contains(next.Source))
                {
                    continue;
                }

                var simMinutes = (next.AtUtc - ai.AtUtc).TotalMinutes * clock.TimeScale;
                if (simMinutes > OverrideWindowSimMinutes)
                {
                    continue;
                }

                var delta = ai.AfterSetpoint is { } a && next.AfterSetpoint is { } u ? u - a : 0;
                result.Add(new OverrideCase(ai.AtUtc, delta));
            }
        }

        return result;
    }

    private (double Offset, Dictionary<string, double> Bias) Replay(List<ComfortFeedbackRecord> feedback, List<OverrideCase> overrides)
    {
        var steps = feedback
            .Select(f => (f.AtUtc, Feedback: (ComfortFeedbackRecord?)f, Override: (OverrideCase?)null))
            .Concat(overrides.Select(o => (o.AtUtc, Feedback: (ComfortFeedbackRecord?)null, Override: (OverrideCase?)o)))
            .OrderBy(s => s.AtUtc);

        double offset = 0;
        var bias = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            var key = ComfortService.ContextKey(clock.ToLocal(step.AtUtc));
            bias.TryGetValue(key, out var current);
            if (step.Feedback is { } f)
            {
                offset = SensitivityModel.Update(offset, f.PmvBase, f.Sensation);
                bias[key] = SensitivityModel.UpdateBias(current, f.Sensation);
            }
            else if (step.Override is { } o)
            {
                bias[key] = SensitivityModel.UpdateBiasFromOverride(current, o.DeltaC);
            }
        }

        return (offset, bias);
    }

    private List<Preference> Preferences(IReadOnlyList<DeviceEventView> all, string memberId, DateTime now)
    {
        var samples = all
            .Where(e => e.MemberId == memberId && e.CommandType == "setpoint" && UserSources.Contains(e.Source) && e.ValueNumber is not null)
            .Select(e =>
            {
                var local = clock.ToLocal(e.AtUtc);
                var temp = e.AfterMode switch { "cool" => "hot", "heat" => "cold", _ => "mild" };
                return (Local: local, Temp: temp, Value: e.ValueNumber!.Value, Age: Math.Max(0, (now - e.AtUtc).TotalDays));
            });

        var result = new List<Preference>();
        foreach (var group in samples.GroupBy(s => (DayType: HomeTime.DayType(s.Local), Slot: HomeTime.Slot(s.Local), s.Temp)))
        {
            var items = group.Select(s => (s.Value, Weight: Math.Pow(0.5, s.Age / HalfLifeDays))).ToList();
            if (items.Count < 3)
            {
                continue;
            }

            var median = WeightedMedian(items);
            var totalWeight = items.Sum(i => i.Weight);
            var share = items.Where(i => Math.Abs(i.Value - median) <= 1.0).Sum(i => i.Weight) / totalWeight;
            var confidence = Math.Round(Math.Min(1, items.Count / 8.0) * share, 2);
            var context = $"{group.Key.DayType} {group.Key.Slot}, {group.Key.Temp}";
            result.Add(new Preference(
                context,
                median,
                confidence,
                items.Count,
                $"{DayTypeText(group.Key.DayType)} {SlotText(group.Key.Slot)} {TempText(group.Key.Temp)} genelde {Num(median)} °C tercih ediyorsunuz ({items.Count} kayıt)."));
        }

        return [.. result.OrderByDescending(p => p.Confidence).ThenBy(p => p.Context, StringComparer.Ordinal)];
    }

    private List<Routine> Routines(IReadOnlyList<DeviceEventView> all, string memberId, DateTime now, DateTime since)
    {
        var windowStart = now.AddDays(-RoutineWindowDays);
        if (since > windowStart)
        {
            windowStart = since;
        }

        var weeks = (int)Math.Ceiling(RoutineWindowDays / 7.0);
        var turnOns = all
            .Where(e => e.MemberId == memberId && e.CommandType == "power" && e.ValueBool == true && UserSources.Contains(e.Source) && e.AtUtc >= windowStart)
            .Select(e => (e.DeviceId, Local: clock.ToLocal(e.AtUtc)))
            .ToList();

        var result = new List<Routine>();
        foreach (var group in turnOns.GroupBy(t => (t.DeviceId, DayType: HomeTime.DayType(t.Local))))
        {
            var eligibleDays = EligibleDays(clock.ToLocal(windowStart), clock.ToLocal(now), group.Key.DayType);
            var points = group.Select(t => (Day: t.Local.Date, Minute: HomeTime.MinuteOfDay(t.Local))).ToList();

            // Best cluster: the candidate center with the most distinct days inside +-20 min.
            var best = points
                .Select(c => points.Where(p => HomeTime.CircularDistanceMin(p.Minute, c.Minute) <= RoutineWindowMin).ToList())
                .OrderByDescending(cluster => cluster.Select(p => p.Day).Distinct().Count())
                .First();
            var hits = best.Select(p => p.Day).Distinct().Count();
            var confidence = Math.Min(0.99, (double)hits / Math.Max(1, eligibleDays));
            if (hits < 4 || confidence < 0.5)
            {
                continue;
            }

            var time = HomeTime.FormatMinute(HomeTime.CircularMeanMin(best.Select(p => p.Minute)));
            result.Add(new Routine(
                $"rt-{memberId}-{group.Key.DeviceId}-{group.Key.DayType}",
                "power_on",
                group.Key.DeviceId,
                group.Key.DayType,
                time,
                RoutineWindowMin,
                Math.Round(confidence, 2),
                $"Son {weeks} haftada {hits} kez {time} civarı açtınız",
                RoutineStatuses.Detected)
            {
                MemberId = memberId,
            });
        }

        return result;
    }

    private List<WeeklyRate> WeeklyOverrideRate(IReadOnlyList<DeviceEventView> all, List<OverrideCase> overrides, string memberId)
    {
        var aiCommands = all.Where(e => e.Source == "ai" && e.MemberId == memberId).Select(e => e.AtUtc).ToList();
        return [.. aiCommands
            .GroupBy(WeekStartUtc)
            .OrderBy(g => g.Key)
            .Select(g => new WeekRate(g.Key, overrides.Count(o => WeekStartUtc(o.AtUtc) == g.Key), g.Count()))
            .Select(w => new WeeklyRate(w.WeekStartUtc, Math.Round((double)w.Part / w.Total, 3)))];
    }

    private List<WeeklyRate> WeeklyGoodFeedback(List<ComfortFeedbackRecord> feedback) =>
        [.. feedback
            .GroupBy(f => WeekStartUtc(f.AtUtc))
            .OrderBy(g => g.Key)
            .Select(g => new WeeklyRate(g.Key, Math.Round((double)g.Count(f => f.Sensation == 0) / g.Count(), 3)))];

    private async Task SuggestRoutinesAsync(string memberId, CancellationToken cancellationToken)
    {
        foreach (var routine in profiles.GetRoutines(memberId).Where(r => r.Status == RoutineStatuses.Detected && r.Confidence >= SuggestMinConfidence))
        {
            var created = await notifications.CreateAsync(
                memberId,
                NotificationKinds.Suggestion,
                NotificationLevels.Important,
                "Yeni bir rutin fark ettim",
                $"{DayTypeText(routine.DayType)} {routine.Time} civarında klimayı açıyorsunuz. {routine.Evidence}. Bunu otomatik yapayım mı?",
                new NotificationAction("accept_routine", routine.Id),
                cancellationToken);
            if (created is not null)
            {
                profiles.UpsertRoutine(routine with { Status = RoutineStatuses.Suggested });
            }
        }
    }

    private DateTime WeekStartUtc(DateTime atUtc) =>
        DateTime.SpecifyKind(HomeTime.WeekStartLocal(clock.ToLocal(atUtc)) - clock.UtcOffset, DateTimeKind.Utc);

    private static int EligibleDays(DateTime fromLocal, DateTime toLocal, string dayType)
    {
        var count = 0;
        for (var day = fromLocal.Date; day <= toLocal.Date; day = day.AddDays(1))
        {
            if (HomeTime.DayType(day) == dayType)
            {
                count++;
            }
        }

        return count;
    }

    private static double WeightedMedian(List<(double Value, double Weight)> items)
    {
        var sorted = items.OrderBy(i => i.Value).ToList();
        var half = sorted.Sum(i => i.Weight) / 2;
        double cumulative = 0;
        foreach (var item in sorted)
        {
            cumulative += item.Weight;
            if (cumulative >= half)
            {
                return item.Value;
            }
        }

        return sorted[^1].Value;
    }

    private static string Num(double value) => value.ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',');

    private static string DayTypeText(string dayType) => dayType == HomeTime.Weekend ? "Hafta sonu" : "Hafta içi";

    private static string SlotText(string slot) =>
        slot switch
        {
            "morning" => "sabah",
            "afternoon" => "öğleden sonra",
            "evening" => "akşam",
            _ => "gece",
        };

    private static string TempText(string temp) =>
        temp switch
        {
            "hot" => "sıcakken",
            "cold" => "soğukken",
            _ => "ılıkken",
        };

    private sealed record OverrideCase(DateTime AtUtc, double DeltaC);

    private sealed record WeekRate(DateTime WeekStartUtc, int Part, int Total);
}

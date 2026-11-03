using System.Text.Json;
using Beko.Agents.Application.Comfort;
using Beko.Agents.Application.Common;
using Beko.Agents.Application.Energy;
using Beko.Agents.Application.Homes;
using Beko.Agents.Application.Settings;
using Beko.Agents.Domain.Homes;

namespace Beko.Agents.Application.Learning;

/// <summary>
/// Generates synthetic history for the demo (deterministic, seeded random): Ayşe (weekdays home at about 18:30 and AC on, 26 °C at 23:00,
/// gets cold easily) and Mehmet (prefers 23 °C, works from home on Fridays). Çeliknaz's own setpoints start two degrees off and
/// converge, so the override rate falls and the good-feedback rate rises over the weeks. Also fills a week of energy history.
/// Times are home-local; stored timestamps are real UTC; gaps between an AI command and an override are real time = simulated
/// minutes / TimeScale, matching how the learner reads them.
/// </summary>
public sealed class HistorySeeder(
    IDeviceEventStore eventStore,
    IComfortFeedbackStore feedbackStore,
    PatternLearner learner,
    MemberProfileStore profiles,
    EnergyTracker energyTracker,
    EnergyModel energyModel,
    IHomeClock clock,
    TimeProvider timeProvider)
{
    private const string Salon = "ac-salon";

    private static readonly string[] UserSources = ["app", "remote", "voice"];

    /// <summary>Writes the history, runs the learner and returns the number of device events written.</summary>
    public async Task<int> SeedAsync(int weeks, CancellationToken cancellationToken)
    {
        if (weeks is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(weeks), "weeks must be between 1 and 12.");
        }

        var rng = new Random(20260101);
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var today = clock.ToLocal(nowUtc).Date;
        var endDay = HomeTime.WeekStartLocal(today); // whole calendar weeks, ending last Sunday
        var firstDay = endDay.AddDays(-weeks * 7);
        var written = 0;

        foreach (var member in new[] { "ayse", "mehmet" })
        {
            profiles.ClearReset(member);
        }

        async Task Event(string source, string member, string type, object value, double? setpoint, bool power, DateTime atUtc, string mode = "cool")
        {
            var ac = new AcState(setpoint ?? 25, mode, "auto", 25.5, 56);
            var before = new ApplianceState(!power, power ? "off" : "running", power ? 0 : 820, atUtc, ac, null, null, null);
            var after = new ApplianceState(power, power ? "running" : "off", power ? 820 : 0, atUtc, ac, null, null, null);
            await eventStore.AddAsync(
                new DeviceEventRecord(Guid.NewGuid().ToString("N"), DemoHome.HomeId, Salon, source, member, type, JsonSerializer.SerializeToElement(value), before, after, atUtc, null, null),
                cancellationToken);
            written++;
        }

        // Deterministic accumulators (a rate r fires on every 1/r-th occasion) keep the weekly series smooth.
        var accumulators = new Dictionary<string, double>(StringComparer.Ordinal);
        bool Fires(string key, double chance)
        {
            accumulators.TryGetValue(key, out var acc);
            acc += chance;
            var fired = acc >= 1;
            accumulators[key] = fired ? acc - 1 : acc;
            return fired;
        }

        async Task Feedback(string member, double setpoint, double wanted, double defaultDirection, DateTime atUtc, DateTime day)
        {
            var season = HomeContextService.SeasonOf(day);
            var clo = ComfortEngine.Clo(season, "normal");
            var pmv = ComfortEngine.Calculate(setpoint, 55, ComfortEngine.AirSpeedMs("auto", true), ComfortEngine.MetForHour(19), clo).Pmv;
            var gap = setpoint - wanted;
            var bad = Fires("bad-" + member, Math.Clamp(0.1 + (0.4 * Math.Abs(gap)), 0.05, 0.9));
            var sensation = !bad ? 0 : gap < 0 ? -1 : gap > 0 ? 1 : (int)defaultDirection;
            await feedbackStore.AddAsync(
                new ComfortFeedbackRecord(Guid.NewGuid().ToString("N"), DemoHome.HomeId, member, Salon, sensation, null, null, setpoint, 55, Math.Round(pmv, 3), clo, atUtc, null, null),
                cancellationToken);
        }

        DateTime Utc(DateTime day, double minuteOfDay) => DateTime.SpecifyKind(day.AddMinutes(minuteOfDay) - clock.UtcOffset, DateTimeKind.Utc);
        double Gauss(double sd) => sd * Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        string UserSource() => UserSources[rng.Next(UserSources.Length)];
        double RealGapMin() => (5 + (rng.NextDouble() * 10)) / clock.TimeScale;

        // One AI decision followed (sometimes) by the member's override and the comfort answer.
        async Task AiDecision(string member, DateTime day, double minute, double aiSetpoint, double wanted, double defaultDirection)
        {
            var aiAt = Utc(day, minute);
            await Event("ai", member, "setpoint", aiSetpoint, aiSetpoint, true, aiAt);
            var overrideChance = Math.Clamp(0.12 + (0.35 * Math.Abs(wanted - aiSetpoint)), 0.1, 0.8);
            var overridden = Fires("override-" + member, overrideChance);
            if (overridden)
            {
                await Event(UserSource(), member, "setpoint", wanted, wanted, true, aiAt.AddMinutes(RealGapMin()));
            }

            await Feedback(member, aiSetpoint, wanted, defaultDirection, aiAt.AddMinutes(3), day);
        }

        for (var day = firstDay; day < endDay; day = day.AddDays(1))
        {
            var week = (int)((day - firstDay).TotalDays / 7);
            var p = weeks > 1 ? week / (weeks - 1.0) : 1.0;
            var isWeekday = HomeTime.DayType(day) == HomeTime.Weekday;

            if (isWeekday)
            {
                // Ayşe: home at 18:30, AC on, Çeliknaz sets it, 26 at night.
                if (rng.NextDouble() < 0.9)
                {
                    var on = 18 * 60 + 30 + Gauss(8);
                    await Event(UserSource(), "ayse", "power", true, 25, true, Utc(day, on));
                    await AiDecision("ayse", day, on + 2, Math.Round((24 + (2 * p)) * 2) / 2, 26, -1);
                }

                if (rng.NextDouble() < 0.85)
                {
                    await Event(rng.NextDouble() < 0.7 ? "app" : "voice", "ayse", "setpoint", 26, 26, true, Utc(day, (23 * 60) + Gauss(10)));
                }

                // Mehmet: 20:00 on, prefers 23. Fridays he works from home and turns it on at 10:30.
                var mehmetOn = day.DayOfWeek == DayOfWeek.Friday ? (10 * 60) + 30 : 20 * 60;
                if (rng.NextDouble() < (day.DayOfWeek == DayOfWeek.Friday ? 0.8 : 0.95))
                {
                    var at = mehmetOn + Gauss(10);
                    await Event("app", "mehmet", "power", true, 23, true, Utc(day, at));
                    await Event("app", "mehmet", "setpoint", 23, 23, true, Utc(day, at + 1));
                    if (day.DayOfWeek != DayOfWeek.Friday)
                    {
                        await AiDecision("mehmet", day, at + 10, Math.Round((25 - (2 * p)) * 2) / 2, 23, 1);
                    }
                }
            }
            else if (rng.NextDouble() < 0.6)
            {
                await Event(UserSource(), "ayse", "power", true, 25, true, Utc(day, (13 * 60) + Gauss(60)));
            }
        }

        // Energy history for the dashboard: the week before today.
        for (var d = 1; d <= 7; d++)
        {
            var day = DateOnly.FromDateTime(today.AddDays(-d));
            var dayStart = today.AddDays(-d);
            var acKwh = Math.Max(1.5, 3.2 + Gauss(0.4));
            var saved = acKwh * 0.1;
            var acTl = energyModel.CostOf(dayStart.AddHours(17), TimeSpan.FromHours(6), acKwh);
            var savedTl = energyModel.CostOf(dayStart.AddHours(17), TimeSpan.FromHours(6), saved);
            energyTracker.AddHistory(day, Salon, acKwh, acTl, saved, savedTl);
            energyTracker.AddHistory(day, "dishwasher-1", 0.9, energyModel.CostOf(dayStart.AddHours(23), TimeSpan.FromHours(3), 0.9), 0, 0);
            energyTracker.AddHistory(day, "washer-1", 0.8, energyModel.CostOf(dayStart.AddHours(10), TimeSpan.FromHours(2), 0.8), 0, 0);
            energyTracker.AddHistory(day, "fridge-1", 1.2, energyModel.CostOf(dayStart, TimeSpan.FromHours(24), 1.2), 0, 0);
        }

        await learner.RunAllAsync(cancellationToken);
        return written;
    }
}

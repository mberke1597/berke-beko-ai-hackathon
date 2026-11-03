using System.Globalization;
using Beko.Agents.Application.Common;
using Beko.Agents.Application.Energy;
using Beko.Agents.Application.Homes;
using Beko.Agents.Application.Learning;
using Beko.Agents.Application.Options;
using Beko.Agents.Application.Presence;
using Microsoft.Extensions.Options;

namespace Beko.Agents.Application.Dashboard;

public sealed record ToolStat(string Tool, long Calls, long Errors);

/// <summary>LLM numbers read from the runtime's <c>runs</c>, <c>llm_calls</c> and <c>tool_calls</c> collections (zeros when empty).</summary>
public sealed record RunStats(
    long RunsToday,
    long ConversationsToday,
    double CostUsdToday,
    double CostUsdMonth,
    long TokensToday,
    double AvgCostUsdPerConversation,
    double AvgLatencyMs,
    IReadOnlyList<ToolStat> Tools)
{
    public static readonly RunStats Empty = new(0, 0, 0, 0, 0, 0, 0, []);
}

/// <summary>Port to the LLM run statistics (Mongo in production).</summary>
public interface IRunStatsReader
{
    Task<RunStats> GetAsync(DateTime todayStartUtc, DateTime monthStartUtc, CancellationToken cancellationToken);
}

public sealed record CommandsToday(int Voice, int App, int Remote, int Ai, int Schedule);

public sealed record LlmSummary(double CostUsdToday, double CostUsdMonth, long TokensToday, double AvgCostUsdPerConversation, double AvgLatencyMs);

public sealed record EnergySaved(double SavedKwhWeek, double SavedTlWeek);

public sealed record LearningSummary(IReadOnlyList<WeeklyRate> OverrideRateTrend, IReadOnlyList<WeeklyRate> GoodFeedbackTrend, double SuggestionAcceptanceRate);

public sealed record ServerSummary(double MonthlyCostTl, double CostPerActiveUserTl);

/// <summary>Wire shape of <c>GET /api/dashboard/summary</c>.</summary>
public sealed record DashboardSummary(
    int Homes,
    int ActiveMembers,
    CommandsToday CommandsToday,
    long ConversationsToday,
    long RunsToday,
    LlmSummary Llm,
    EnergySaved Energy,
    LearningSummary Learning,
    IReadOnlyList<ToolStat> Tools,
    ServerSummary Server);

/// <summary>Wire shape of <c>GET /api/dashboard/projection</c>.</summary>
public sealed record DashboardProjection(
    int Homes,
    double LlmCostUsdMonth,
    double ServerCostTlMonth,
    double SavedKwhMonth,
    double SavedTlMonth,
    IReadOnlyList<string> Assumptions);

/// <summary>Numbers of the Arçelik dashboard from commands, LLM runs, energy, learning and infra cost options.</summary>
public sealed class DashboardService(
    IRunStatsReader runStats,
    IDeviceEventReader events,
    EnergyTracker energy,
    MemberProfileStore profiles,
    HomeDirectory directory,
    PresenceStore presence,
    IHomeClock clock,
    TimeProvider timeProvider,
    IOptions<InfraCostOptions> infra)
{
    public async Task<DashboardSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var (todayStart, monthStart) = Boundaries();
        var home = await directory.GetAsync(cancellationToken);
        var weekEvents = await events.GetAsync(DemoHome.HomeId, todayStart.AddDays(-6), cancellationToken);
        var today = weekEvents.Where(e => e.AtUtc >= todayStart).ToList();
        var stats = await runStats.GetAsync(todayStart, monthStart, cancellationToken);
        var saved = energy.Summary();

        var activeMembers = weekEvents.Select(e => e.MemberId).Distinct().Count();
        if (activeMembers == 0)
        {
            activeMembers = home.Members.Count(m => presence.Band(m.Id) == PresenceBands.Home);
        }

        var overrides = Trend(home.Members.Select(m => profiles.GetOverrideRate(m.Id)));
        var good = Trend(home.Members.Select(m => profiles.GetGoodFeedbackRate(m.Id)));
        var routines = profiles.AllRoutines();
        var accepted = routines.Count(r => r.Status == RoutineStatuses.Accepted);
        var decided = accepted + routines.Count(r => r.Status == RoutineStatuses.Rejected);

        return new DashboardSummary(
            1,
            activeMembers,
            new CommandsToday(Count(today, "voice"), Count(today, "app"), Count(today, "remote"), Count(today, "ai"), Count(today, "schedule")),
            stats.ConversationsToday,
            stats.RunsToday,
            new LlmSummary(stats.CostUsdToday, stats.CostUsdMonth, stats.TokensToday, stats.AvgCostUsdPerConversation, stats.AvgLatencyMs),
            new EnergySaved(saved.SavedWeekKwh, saved.SavedWeekTl),
            new LearningSummary(overrides, good, decided == 0 ? 0 : Math.Round((double)accepted / decided, 2)),
            stats.Tools,
            new ServerSummary(infra.Value.MonthlyServerCostTl, Math.Round(infra.Value.MonthlyServerCostTl / Math.Max(1, activeMembers), 2)));
    }

    /// <summary>Linear extrapolation of the demo home's per-home averages to <paramref name="homes"/> homes.</summary>
    public async Task<DashboardProjection> GetProjectionAsync(int homes, CancellationToken cancellationToken)
    {
        var (todayStart, monthStart) = Boundaries();
        var stats = await runStats.GetAsync(todayStart, monthStart, cancellationToken);
        var saved = energy.Summary();
        var elapsedDays = Math.Max(1, (timeProvider.GetUtcNow().UtcDateTime.Date - monthStart.Date).Days + 1);
        var llmPerHomeMonth = stats.CostUsdMonth / elapsedDays * 30;
        var servers = Math.Ceiling((double)homes / infra.Value.HomesPerServer);
        var savedKwhMonth = saved.SavedWeekKwh * 30 / 7 * homes;
        var savedTlMonth = saved.SavedWeekTl * 30 / 7 * homes;
        var inv = CultureInfo.InvariantCulture;
        return new DashboardProjection(
            homes,
            Math.Round(llmPerHomeMonth * homes, 2),
            Math.Round(servers * infra.Value.MonthlyServerCostTl, 2),
            Math.Round(savedKwhMonth, 1),
            Math.Round(savedTlMonth, 1),
            [
                $"Demo evin ayda ortalama LLM maliyeti {llmPerHomeMonth.ToString("0.00", inv)} USD (bu ayın ortalamasından 30 güne uzatıldı), tüm evler aynı kullanımda varsayıldı.",
                $"Sunucu maliyeti: bir sunucu {infra.Value.HomesPerServer} eve hizmet eder, aylık {infra.Value.MonthlyServerCostTl.ToString("0", inv)} TL; {servers.ToString("0", inv)} sunucu gerekir.",
                $"Tasarruf: demo evin son 7 gündeki tahmini tasarrufu ({saved.SavedWeekKwh.ToString("0.0", inv)} kWh) 30/7 ile aya çevrildi ve ev sayısıyla çarpıldı.",
                "Tasarruf tahmini, Çeliknaz kararları olmadan aynı saatlerdeki alışılmış ayarlara göre hesaplanır; gerçek kullanım evden eve değişir.",
            ]);
    }

    private (DateTime TodayStartUtc, DateTime MonthStartUtc) Boundaries()
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var localToday = clock.ToLocal(nowUtc).Date;
        var todayStart = DateTime.SpecifyKind(localToday - clock.UtcOffset, DateTimeKind.Utc);
        var monthStart = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return (todayStart, monthStart);
    }

    private static int Count(List<DeviceEventView> list, string source) => list.Count(e => e.Source == source);

    private static List<WeeklyRate> Trend(IEnumerable<IReadOnlyList<WeeklyRate>> series) =>
        [.. series
            .SelectMany(s => s)
            .GroupBy(w => w.WeekStartUtc)
            .OrderBy(g => g.Key)
            .Select(g => new WeeklyRate(g.Key, Math.Round(g.Average(w => w.Rate), 3)))];
}

using System.Text.Json.Serialization;
using Beko.Agents.Application.Common;
using Beko.Agents.Application.Homes;
using Beko.Agents.Application.Notifications;
using Beko.Agents.Application.Presence;

namespace Beko.Agents.Application.Tasks;

/// <summary>A household chore. <see cref="Origin"/> is <c>device_event|routine|manual|voice</c>, <see cref="Status"/> <c>open|done|skipped</c>.</summary>
public sealed record HouseholdTask(
    string Id,
    string Title,
    string Origin,
    string? ApplianceId,
    string AssignedMemberId,
    string Status,
    int EffortPoints,
    DateTime CreatedAtUtc,
    DateTime? DueAtUtc,
    string Reason)
{
    [JsonIgnore]
    public DateTime? CompletedAtUtc { get; init; }
}

/// <summary>Creates a task from a product event. <see cref="Trigger"/> is <c>{applianceType}.{event}</c>.</summary>
public sealed record TaskRule(string Id, string Trigger, string Template, string Strategy, string? FixedMemberId, int EffortPoints, bool Enabled);

public sealed record TaskRuleUpdate(string? Strategy, string? FixedMemberId, string? Template, int? EffortPoints, bool? Enabled);

public sealed record TaskStat(string MemberId, int PointsLast7Days, int DoneLast7Days);

public static class TaskStrategies
{
    public const string FairShare = "fair_share";
    public const string RoundRobin = "round_robin";
    public const string Fixed = "fixed";
    public const string Ask = "ask";

    public static readonly IReadOnlyList<string> All = [FairShare, RoundRobin, Fixed, Ask];
}

/// <summary>
/// Household tasks and their distribution. Strategies: <c>fair_share</c> (among members at home, else all: the lowest load wins,
/// ties by the least recent task; load is the points done in the last 7 days plus the points still open, so two chores in a row
/// do not land on the same person), <c>round_robin</c>, <c>fixed</c>, and <c>ask</c> (no one to ask in the demo, so it falls
/// back to <c>fair_share</c>).
/// </summary>
public sealed class TaskService(
    PresenceStore presence,
    HomeDirectory directory,
    IHomeClock clock,
    NotificationService notifications,
    IHomeEventPublisher publisher,
    IHomeWhizClient homeWhiz) : IApplianceEventObserver
{
    private readonly Lock _gate = new();
    private readonly List<HouseholdTask> _tasks = [];
    private readonly Dictionary<string, int> _roundRobinLast = new(StringComparer.Ordinal);
    private readonly List<TaskRule> _rules =
    [
        new("rule-washer-finished", "washing_machine.finished", "Çamaşırları as", TaskStrategies.FairShare, null, 2, true),
        new("rule-dishwasher-finished", "dishwasher.finished", "Bulaşık makinesini boşalt", TaskStrategies.FairShare, null, 2, true),
        new("rule-oven-timer-done", "oven.timer_done", "Fırını kontrol et", TaskStrategies.FairShare, null, 1, false),
    ];

    public IReadOnlyList<HouseholdTask> List(string? status)
    {
        lock (_gate)
        {
            return [.. _tasks.Where(t => status is null || t.Status == status).OrderByDescending(t => t.CreatedAtUtc)];
        }
    }

    public IReadOnlyList<TaskRule> Rules()
    {
        lock (_gate)
        {
            return [.. _rules];
        }
    }

    /// <summary>Returns the error text, or <c>null</c> on success; <c>not_found</c> when the rule does not exist.</summary>
    public async Task<(TaskRule? Rule, string? Error)> UpdateRuleAsync(string id, TaskRuleUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Strategy is not null && !TaskStrategies.All.Contains(update.Strategy))
        {
            return (null, "strategy must be fair_share, round_robin, fixed or ask.");
        }

        if (update.EffortPoints is < 0 or > 20)
        {
            return (null, "effortPoints must be between 0 and 20.");
        }

        if (update.FixedMemberId is not null && !await directory.IsMemberAsync(update.FixedMemberId, cancellationToken))
        {
            return (null, "fixedMemberId is not a member of the home.");
        }

        lock (_gate)
        {
            var index = _rules.FindIndex(r => r.Id == id);
            if (index < 0)
            {
                return (null, "not_found");
            }

            var current = _rules[index];
            var updated = current with
            {
                Strategy = update.Strategy ?? current.Strategy,
                FixedMemberId = update.FixedMemberId ?? current.FixedMemberId,
                Template = string.IsNullOrWhiteSpace(update.Template) ? current.Template : update.Template,
                EffortPoints = update.EffortPoints ?? current.EffortPoints,
                Enabled = update.Enabled ?? current.Enabled,
            };
            _rules[index] = updated;
            return (updated, null);
        }
    }

    public async Task<IReadOnlyList<TaskStat>> StatsAsync(CancellationToken cancellationToken)
    {
        var members = await directory.MemberIdsAsync(cancellationToken);
        var since = clock.UtcNow.AddDays(-7);
        lock (_gate)
        {
            return
            [
                .. members.Select(m =>
                {
                    var done = _tasks.Where(t => t.AssignedMemberId == m && t.Status == "done" && t.CompletedAtUtc >= since).ToList();
                    return new TaskStat(m, done.Sum(t => t.EffortPoints), done.Count);
                }),
            ];
        }
    }

    /// <summary>Creates a task. A <c>null</c> assignee is distributed by <c>fair_share</c>. Returns <c>null</c> for an unknown assignee.</summary>
    public async Task<HouseholdTask?> CreateAsync(
        string title,
        string? assignedMemberId,
        int effortPoints,
        string origin,
        CancellationToken cancellationToken)
    {
        var members = await directory.MemberIdsAsync(cancellationToken);
        if (assignedMemberId is not null && !members.Contains(assignedMemberId))
        {
            return null;
        }

        return await AddAsync(title, origin, null, effortPoints, assignedMemberId, null, members, cancellationToken);
    }

    public HouseholdTask? Complete(string id, bool done)
    {
        lock (_gate)
        {
            var index = _tasks.FindIndex(t => t.Id == id);
            if (index < 0)
            {
                return null;
            }

            _tasks[index] = _tasks[index] with { Status = done ? "done" : "skipped", CompletedAtUtc = clock.UtcNow };
            return _tasks[index];
        }
    }

    /// <summary>Hands a task to a member, or (<c>null</c>) redistributes it by <c>fair_share</c> excluding the current assignee. Returns <c>null</c> when not found or the member is unknown.</summary>
    public async Task<HouseholdTask?> ReassignAsync(string id, string? memberId, CancellationToken cancellationToken)
    {
        var members = await directory.MemberIdsAsync(cancellationToken);
        if (memberId is not null && !members.Contains(memberId))
        {
            return null;
        }

        lock (_gate)
        {
            var index = _tasks.FindIndex(t => t.Id == id);
            if (index < 0)
            {
                return null;
            }

            var task = _tasks[index];
            string target;
            string reason;
            if (memberId is not null)
            {
                (target, reason) = (memberId, "Elle devredildi");
            }
            else
            {
                var others = members.Where(m => m != task.AssignedMemberId).ToList();
                (target, reason) = FairShare(others.Count > 0 ? others : members, id);
            }

            _tasks[index] = task with { AssignedMemberId = target, Reason = reason, Status = "open" };
            return _tasks[index];
        }
    }

    public async Task OnApplianceEventAsync(string homeId, string applianceId, string eventName, DateTime atUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eventName);
        var appliance = await homeWhiz.GetApplianceAsync(applianceId, cancellationToken);
        if (appliance is null)
        {
            return;
        }

        TaskRule? rule;
        lock (_gate)
        {
            rule = _rules.FirstOrDefault(r => r.Enabled && r.Trigger == $"{appliance.Type}.{eventName}");
        }

        if (rule is null)
        {
            return;
        }

        var members = await directory.MemberIdsAsync(cancellationToken);
        await AddAsync(rule.Template, "device_event", applianceId, rule.EffortPoints, null, rule, members, cancellationToken);
    }

    private async Task<HouseholdTask> AddAsync(
        string title,
        string origin,
        string? applianceId,
        int effortPoints,
        string? assignedMemberId,
        TaskRule? rule,
        IReadOnlyList<string> members,
        CancellationToken cancellationToken)
    {
        HouseholdTask task;
        lock (_gate)
        {
            var (assignee, reason) = assignedMemberId is not null
                ? (assignedMemberId, "Elle atandı")
                : Distribute(rule, members);
            task = new HouseholdTask(
                Guid.NewGuid().ToString("N"),
                title,
                origin,
                applianceId,
                assignee,
                "open",
                effortPoints,
                clock.UtcNow,
                null,
                reason);
            _tasks.Add(task);
        }

        await publisher.TaskAssignedAsync(DemoHome.HomeId, task, cancellationToken);
        await notifications.CreateAsync(
            task.AssignedMemberId,
            NotificationKinds.Task,
            NotificationLevels.Important,
            "Yeni görev",
            $"{task.Title}. {task.Reason}.",
            null,
            cancellationToken);
        return task;
    }

    private (string MemberId, string Reason) Distribute(TaskRule? rule, IReadOnlyList<string> members)
    {
        var key = rule?.Id ?? "manual";
        switch (rule?.Strategy)
        {
            case TaskStrategies.RoundRobin:
                var next = (_roundRobinLast.GetValueOrDefault(key, -1) + 1) % members.Count;
                _roundRobinLast[key] = next;
                return (members[next], "Sıralı dağıtım: sıradaki kişi");
            case TaskStrategies.Fixed when rule.FixedMemberId is { } fixedId && members.Contains(fixedId):
                return (fixedId, "Sabit atama");
            case TaskStrategies.Ask:
                var (asked, why) = FairShare(members, key);
                return (asked, $"Soru yanıtsız kaldı, {char.ToLowerInvariant(why[0])}{why[1..]}");
            default:
                return FairShare(members, key);
        }
    }

    private (string MemberId, string Reason) FairShare(IReadOnlyList<string> members, string excludeTaskId)
    {
        var home = members.Where(m => presence.Band(m) == PresenceBands.Home).ToList();
        var candidates = home.Count > 0 ? home : members.ToList();
        var since = clock.UtcNow.AddDays(-7);

        int Load(string m) =>
            _tasks.Where(t => t.AssignedMemberId == m && t.Id != excludeTaskId
                && ((t.Status == "done" && t.CompletedAtUtc >= since) || t.Status == "open")).Sum(t => t.EffortPoints);

        DateTime LastTask(string m) =>
            _tasks.Where(t => t.AssignedMemberId == m && t.Id != excludeTaskId).Select(t => t.CreatedAtUtc).DefaultIfEmpty(DateTime.MinValue).Max();

        var winner = candidates.OrderBy(Load).ThenBy(LastTask).ThenBy(m => m, StringComparer.Ordinal).First();
        return (winner, home.Count > 0 ? "Adil dağıtım: evde olanlar arasında son 7 günde en az iş yapan" : "Adil dağıtım: son 7 günde en az iş yapan");
    }
}

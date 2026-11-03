using Beko.Agents.Application.Common;
using Beko.Agents.Application.Homes;
using Beko.Agents.Application.Settings;

namespace Beko.Agents.Application.Notifications;

/// <summary>An in-app notification. <see cref="Kind"/> is <c>info|suggestion|comfort|task|alert</c>, <see cref="Level"/> is <c>critical|important|info</c>.</summary>
public sealed record Notification(
    string Id,
    string MemberId,
    string Kind,
    string Level,
    string Title,
    string Text,
    DateTime CreatedAtUtc,
    bool Read,
    NotificationAction? Action);

public sealed record NotificationAction(string Type, string RoutineId);

public static class NotificationKinds
{
    public const string Info = "info";
    public const string Suggestion = "suggestion";
    public const string Comfort = "comfort";
    public const string Task = "task";
    public const string Alert = "alert";
}

public static class NotificationLevels
{
    public const string Critical = "critical";
    public const string Important = "important";
    public const string Info = "info";
}

/// <summary>
/// Stores and delivers notifications. Delivery honours the member's notification level (quiet: critical only, balanced:
/// critical and important, detailed: all), quiet hours (critical only) and a cap of 3 suggestions per simulated day.
/// </summary>
public sealed class NotificationService(
    SettingsService settings,
    IHomeClock clock,
    IHomeEventPublisher publisher)
{
    public const int DailySuggestionCap = 3;

    private readonly Lock _gate = new();
    private readonly List<Notification> _items = [];

    /// <summary>Creates the notification when the member's settings allow it; returns <c>null</c> when it was suppressed.</summary>
    public async Task<Notification?> CreateAsync(
        string memberId,
        string kind,
        string level,
        string title,
        string text,
        NotificationAction? action,
        CancellationToken cancellationToken)
    {
        var memberSettings = settings.Get(memberId);
        var nowUtc = clock.UtcNow;
        Notification notification;
        lock (_gate)
        {
            if (!ShouldDeliver(memberSettings, kind, level, nowUtc))
            {
                return null;
            }

            notification = new Notification(Guid.NewGuid().ToString("N"), memberId, kind, level, title, text, nowUtc, false, action);
            _items.Add(notification);
            if (_items.Count > 500)
            {
                _items.RemoveAt(0);
            }
        }

        await publisher.NotificationCreatedAsync(DemoHome.HomeId, notification, cancellationToken);
        return notification;
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<Notification> List(string memberId)
    {
        lock (_gate)
        {
            return [.. _items.Where(n => n.MemberId == memberId).OrderByDescending(n => n.CreatedAtUtc)];
        }
    }

    public Notification? MarkRead(string id)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(n => n.Id == id);
            if (index < 0)
            {
                return null;
            }

            _items[index] = _items[index] with { Read = true };
            return _items[index];
        }
    }

    private bool ShouldDeliver(MemberSettings memberSettings, string kind, string level, DateTime nowUtc)
    {
        var allowedByLevel = memberSettings.NotificationLevel switch
        {
            NotificationLevelSetting.Quiet => level == NotificationLevels.Critical,
            NotificationLevelSetting.Balanced => level is NotificationLevels.Critical or NotificationLevels.Important,
            _ => true,
        };
        if (!allowedByLevel)
        {
            return false;
        }

        var local = clock.ToLocal(nowUtc);
        if (level != NotificationLevels.Critical && memberSettings.QuietHours.Contains(local.TimeOfDay))
        {
            return false;
        }

        if (kind == NotificationKinds.Suggestion)
        {
            var today = local.Date;
            var count = _items.Count(n => n.MemberId == memberSettings.MemberId && n.Kind == NotificationKinds.Suggestion && clock.ToLocal(n.CreatedAtUtc).Date == today);
            if (count >= DailySuggestionCap)
            {
                return false;
            }
        }

        return true;
    }
}

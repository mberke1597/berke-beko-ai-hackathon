using System.Globalization;
using Beko.Agents.Application.Energy;
using Beko.Agents.Application.Learning;

namespace Beko.Agents.Application.Settings;

public static class PriorityModes
{
    public const string Saving = "saving";
    public const string Balanced = "balanced";
    public const string Comfort = "comfort";

    public static readonly IReadOnlyList<string> All = [Saving, Balanced, Comfort];

    /// <summary>Comfort band as |PMV|: saving 1.0, balanced 0.7, comfort 0.5.</summary>
    public static double BandPmv(string mode) =>
        mode switch
        {
            Comfort => 0.5,
            Balanced => 0.7,
            _ => 1.0,
        };
}

public static class NotificationLevelSetting
{
    public const string Quiet = "quiet";
    public const string Balanced = "balanced";
    public const string Detailed = "detailed";

    public static readonly IReadOnlyList<string> All = [Quiet, Balanced, Detailed];
}

public static class ClothingSetting
{
    public const string Light = "light";
    public const string Normal = "normal";
    public const string Heavy = "heavy";

    public static readonly IReadOnlyList<string> All = [Light, Normal, Heavy];
}

/// <summary>Quiet hours in home-local time; the window may wrap midnight.</summary>
public sealed record QuietHours(string From, string To)
{
    public bool Contains(TimeSpan localTimeOfDay)
    {
        var from = Parse(From);
        var to = Parse(To);
        if (from == to)
        {
            return false;
        }

        return from < to ? localTimeOfDay >= from && localTimeOfDay < to : localTimeOfDay >= from || localTimeOfDay < to;
    }

    public static bool IsValid(string? value) => TimeSpan.TryParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture, out _);

    private static TimeSpan Parse(string value) => TimeSpan.ParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture);
}

public sealed record MemberSettings(
    string MemberId,
    string PriorityMode,
    string NotificationLevel,
    QuietHours QuietHours,
    string Clothing,
    bool LocationSharing);

/// <summary>Wire shape of <c>GET /api/me/settings</c>.</summary>
public sealed record MemberSettingsView(
    string MemberId,
    string PriorityMode,
    string NotificationLevel,
    QuietHours QuietHours,
    string Clothing,
    bool LocationSharing,
    double SensitivityOffset,
    IReadOnlyDictionary<string, double> EstimatedMonthlyTl);

/// <summary>Writable fields; <c>null</c> keeps the current value.</summary>
public sealed record MemberSettingsUpdate(
    string? PriorityMode,
    string? NotificationLevel,
    QuietHours? QuietHours,
    string? Clothing,
    bool? LocationSharing);

/// <summary>Per-member settings in memory with the defaults of the contract (saving, balanced, normal, 23:00-08:00).</summary>
public sealed class SettingsService(MemberProfileStore profiles, EnergyModel energy)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, MemberSettings> _settings = new(StringComparer.Ordinal);

    public MemberSettings Get(string memberId)
    {
        lock (_gate)
        {
            return _settings.TryGetValue(memberId, out var existing) ? existing : Default(memberId);
        }
    }

    public MemberSettingsView GetView(string memberId)
    {
        var s = Get(memberId);
        return new MemberSettingsView(
            s.MemberId,
            s.PriorityMode,
            s.NotificationLevel,
            s.QuietHours,
            s.Clothing,
            s.LocationSharing,
            Math.Round(profiles.GetOffset(memberId), 2),
            PriorityModes.All.ToDictionary(m => m, energy.EstimatedMonthlyTl, StringComparer.Ordinal));
    }

    /// <summary>Applies the update. Returns the validation error text, or <c>null</c> on success.</summary>
    public string? Update(string memberId, MemberSettingsUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (update.PriorityMode is not null && !PriorityModes.All.Contains(update.PriorityMode))
        {
            return "priorityMode must be saving, balanced or comfort.";
        }

        if (update.NotificationLevel is not null && !NotificationLevelSetting.All.Contains(update.NotificationLevel))
        {
            return "notificationLevel must be quiet, balanced or detailed.";
        }

        if (update.Clothing is not null && !ClothingSetting.All.Contains(update.Clothing))
        {
            return "clothing must be light, normal or heavy.";
        }

        if (update.QuietHours is not null && (!QuietHours.IsValid(update.QuietHours.From) || !QuietHours.IsValid(update.QuietHours.To)))
        {
            return "quietHours must use HH:mm.";
        }

        lock (_gate)
        {
            var current = _settings.TryGetValue(memberId, out var existing) ? existing : Default(memberId);
            _settings[memberId] = current with
            {
                PriorityMode = update.PriorityMode ?? current.PriorityMode,
                NotificationLevel = update.NotificationLevel ?? current.NotificationLevel,
                QuietHours = update.QuietHours ?? current.QuietHours,
                Clothing = update.Clothing ?? current.Clothing,
                LocationSharing = update.LocationSharing ?? current.LocationSharing,
            };
        }

        return null;
    }

    private static MemberSettings Default(string memberId) =>
        new(memberId, PriorityModes.Saving, NotificationLevelSetting.Balanced, new QuietHours("23:00", "08:00"), ClothingSetting.Normal, true);
}

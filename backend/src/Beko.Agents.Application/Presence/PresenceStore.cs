namespace Beko.Agents.Application.Presence;

public static class PresenceBands
{
    public const string Home = "home";
    public const string Near = "near";
    public const string Away = "away";
}

/// <summary>Only the distance band and a timestamp are kept (KVKK: no coordinates).</summary>
public sealed record PresenceEntry(string MemberId, string Band, double DistanceM, double? EtaMin, DateTime UpdatedAtUtc);

/// <summary>Current presence band of each member. Everyone starts <c>home</c>.</summary>
public sealed class PresenceStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PresenceEntry> _entries = new(StringComparer.Ordinal);

    public PresenceEntry Get(string memberId, DateTime nowUtc)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(memberId, out var entry) ? entry : new PresenceEntry(memberId, PresenceBands.Home, 0, null, nowUtc);
        }
    }

    public string Band(string memberId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(memberId, out var entry) ? entry.Band : PresenceBands.Home;
        }
    }

    public void Set(PresenceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _entries[entry.MemberId] = entry;
        }
    }
}

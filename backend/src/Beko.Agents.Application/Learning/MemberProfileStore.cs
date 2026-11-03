using System.Text.Json.Serialization;

namespace Beko.Agents.Application.Learning;

/// <summary>A learned preferred setpoint for a context such as "weekday evening, hot".</summary>
public sealed record Preference(string Context, double Setpoint, double Confidence, int Samples, string Text);

public sealed record WeeklyRate(DateTime WeekStartUtc, double Rate);

/// <summary>
/// A recurring action found in the history. <see cref="Status"/> is <c>detected|suggested|accepted|rejected</c>.
/// Accepting a routine makes it a schedule: the scheduler runs it at <see cref="Time"/> on matching days.
/// </summary>
public sealed record Routine(
    string Id,
    string Action,
    string ApplianceId,
    string DayType,
    string Time,
    int WindowMin,
    double Confidence,
    string Evidence,
    string Status)
{
    [JsonIgnore]
    public string MemberId { get; init; } = string.Empty;
}

public static class RoutineStatuses
{
    public const string Detected = "detected";
    public const string Suggested = "suggested";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
}

/// <summary>Wire shape of <c>GET /api/me/profile</c>.</summary>
public sealed record MemberProfileView(
    double SensitivityOffset,
    IReadOnlyList<Preference> Preferences,
    IReadOnlyList<Routine> Routines,
    IReadOnlyList<WeeklyRate> OverrideRate,
    IReadOnlyList<WeeklyRate> GoodFeedbackRate);

/// <summary>What one learner run computed for a member.</summary>
public sealed record LearnedProfile(
    double SensitivityOffset,
    IReadOnlyDictionary<string, double> Bias,
    IReadOnlyList<Preference> Preferences,
    IReadOnlyList<Routine> Routines,
    IReadOnlyList<WeeklyRate> OverrideRate,
    IReadOnlyList<WeeklyRate> GoodFeedbackRate);

/// <summary>In-memory learned state per member: sensitivity offset, setpoint bias per context, preferences and routines. Thread-safe.</summary>
public sealed class MemberProfileStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, State> _members = new(StringComparer.Ordinal);

    public double GetOffset(string memberId)
    {
        lock (_gate)
        {
            return _members.TryGetValue(memberId, out var s) ? s.SensitivityOffset : 0;
        }
    }

    /// <summary>Learned setpoint bias in °C for a context key (<c>{dayType}.{slot}</c>).</summary>
    public double GetBias(string memberId, string contextKey)
    {
        lock (_gate)
        {
            return _members.TryGetValue(memberId, out var s) && s.Bias.TryGetValue(contextKey, out var bias) ? bias : 0;
        }
    }

    public DateTime? GetResetAtUtc(string memberId)
    {
        lock (_gate)
        {
            return _members.TryGetValue(memberId, out var s) ? s.ResetAtUtc : null;
        }
    }

    public void SetOffset(string memberId, double offset)
    {
        lock (_gate)
        {
            StateOf(memberId).SensitivityOffset = offset;
        }
    }

    public void SetBias(string memberId, string contextKey, double bias)
    {
        lock (_gate)
        {
            StateOf(memberId).Bias[contextKey] = bias;
        }
    }

    public MemberProfileView GetView(string memberId)
    {
        lock (_gate)
        {
            var s = StateOf(memberId);
            return new MemberProfileView(
                Math.Round(s.SensitivityOffset, 2),
                [.. s.Preferences],
                [.. s.Routines.Values.OrderBy(r => r.Id, StringComparer.Ordinal)],
                [.. s.OverrideRate],
                [.. s.GoodFeedbackRate]);
        }
    }

    public IReadOnlyList<Routine> GetRoutines(string memberId)
    {
        lock (_gate)
        {
            return [.. StateOf(memberId).Routines.Values];
        }
    }

    public IReadOnlyList<Routine> AllRoutines()
    {
        lock (_gate)
        {
            return [.. _members.Values.SelectMany(s => s.Routines.Values)];
        }
    }

    public Routine? FindRoutine(string routineId)
    {
        lock (_gate)
        {
            return _members.Values.Select(s => s.Routines.GetValueOrDefault(routineId)).FirstOrDefault(r => r is not null);
        }
    }

    /// <summary>Adds or replaces a routine (manual proposals and status changes).</summary>
    public void UpsertRoutine(Routine routine)
    {
        ArgumentNullException.ThrowIfNull(routine);
        lock (_gate)
        {
            StateOf(routine.MemberId).Routines[routine.Id] = routine;
        }
    }

    public IReadOnlyList<WeeklyRate> GetOverrideRate(string memberId)
    {
        lock (_gate)
        {
            return [.. StateOf(memberId).OverrideRate];
        }
    }

    public IReadOnlyList<WeeklyRate> GetGoodFeedbackRate(string memberId)
    {
        lock (_gate)
        {
            return [.. StateOf(memberId).GoodFeedbackRate];
        }
    }

    /// <summary>
    /// Replaces the learned values. Routines keep a user decision (<c>accepted</c>/<c>rejected</c>) and an earlier
    /// <c>suggested</c> status; routines no longer found disappear unless the user decided on them.
    /// </summary>
    public void Apply(string memberId, LearnedProfile learned)
    {
        ArgumentNullException.ThrowIfNull(learned);
        lock (_gate)
        {
            var s = StateOf(memberId);
            s.SensitivityOffset = learned.SensitivityOffset;
            s.Bias.Clear();
            foreach (var (key, value) in learned.Bias)
            {
                s.Bias[key] = value;
            }

            s.Preferences = [.. learned.Preferences];
            s.OverrideRate = [.. learned.OverrideRate];
            s.GoodFeedbackRate = [.. learned.GoodFeedbackRate];

            var merged = new Dictionary<string, Routine>(StringComparer.Ordinal);
            foreach (var routine in learned.Routines)
            {
                var status = s.Routines.TryGetValue(routine.Id, out var old) && old.Status != RoutineStatuses.Detected ? old.Status : routine.Status;
                merged[routine.Id] = routine with { Status = status, MemberId = memberId };
            }

            foreach (var old in s.Routines.Values.Where(r => !merged.ContainsKey(r.Id) && r.Status is RoutineStatuses.Accepted or RoutineStatuses.Rejected))
            {
                merged[old.Id] = old;
            }

            s.Routines = merged;
        }
    }

    /// <summary>Forgets everything and remembers when, so older events and feedback are ignored from now on.</summary>
    public void Reset(string memberId, DateTime nowUtc)
    {
        lock (_gate)
        {
            _members[memberId] = new State { ResetAtUtc = nowUtc };
        }
    }

    /// <summary>Makes older history count again (a demo seed adds past events after an earlier reset).</summary>
    public void ClearReset(string memberId)
    {
        lock (_gate)
        {
            StateOf(memberId).ResetAtUtc = null;
        }
    }

    private State StateOf(string memberId)
    {
        if (!_members.TryGetValue(memberId, out var s))
        {
            s = new State();
            _members[memberId] = s;
        }

        return s;
    }

    private sealed class State
    {
        public double SensitivityOffset { get; set; }

        public Dictionary<string, double> Bias { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<Preference> Preferences { get; set; } = [];

        public Dictionary<string, Routine> Routines { get; set; } = new(StringComparer.Ordinal);

        public IReadOnlyList<WeeklyRate> OverrideRate { get; set; } = [];

        public IReadOnlyList<WeeklyRate> GoodFeedbackRate { get; set; } = [];

        public DateTime? ResetAtUtc { get; set; }
    }
}

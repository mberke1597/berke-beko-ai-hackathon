namespace Beko.Agents.Application.Common;

/// <summary>Simulated time of the home (starts at real time, runs <see cref="TimeScale"/> times faster).</summary>
public interface IHomeClock
{
    DateTime UtcNow { get; }

    /// <summary>Simulated seconds per real second.</summary>
    double TimeScale { get; }

    /// <summary>Home-local time minus UTC.</summary>
    TimeSpan UtcOffset { get; }
}

public static class HomeClockExtensions
{
    public static DateTime ToLocal(this IHomeClock clock, DateTime utc)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return DateTime.SpecifyKind(utc, DateTimeKind.Utc).Add(clock.UtcOffset);
    }

    public static DateTime LocalNow(this IHomeClock clock) => clock.ToLocal(clock.UtcNow);
}

/// <summary>Helpers for day type, time slots, circular time and weeks in home-local time.</summary>
public static class HomeTime
{
    public const string Weekday = "weekday";

    public const string Weekend = "weekend";

    public static string DayType(DateTime local) =>
        local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? Weekend : Weekday;

    /// <summary>morning 06-12, afternoon 12-18, evening 18-22, night 22-06.</summary>
    public static string Slot(DateTime local) =>
        local.Hour switch
        {
            >= 6 and < 12 => "morning",
            >= 12 and < 18 => "afternoon",
            >= 18 and < 22 => "evening",
            _ => "night",
        };

    /// <summary>Minutes since local midnight.</summary>
    public static double MinuteOfDay(DateTime local) => local.TimeOfDay.TotalMinutes;

    /// <summary>Shortest distance between two minutes of the day on a 24 h circle (23:50 and 00:10 are 20 apart).</summary>
    public static double CircularDistanceMin(double a, double b)
    {
        var d = Math.Abs(a - b) % 1440;
        return d > 720 ? 1440 - d : d;
    }

    /// <summary>Circular mean of minutes of the day.</summary>
    public static double CircularMeanMin(IEnumerable<double> minutes)
    {
        double sin = 0, cos = 0;
        foreach (var m in minutes)
        {
            var angle = m / 1440 * 2 * Math.PI;
            sin += Math.Sin(angle);
            cos += Math.Cos(angle);
        }

        var mean = Math.Atan2(sin, cos) / (2 * Math.PI) * 1440;
        return mean < 0 ? mean + 1440 : mean;
    }

    public static string FormatMinute(double minuteOfDay)
    {
        var total = (int)Math.Round(minuteOfDay) % 1440;
        return $"{total / 60:00}:{total % 60:00}";
    }

    /// <summary>Monday 00:00 of the week containing <paramref name="local"/> (still home-local).</summary>
    public static DateTime WeekStartLocal(DateTime local)
    {
        var daysSinceMonday = ((int)local.DayOfWeek + 6) % 7;
        return local.Date.AddDays(-daysSinceMonday);
    }
}

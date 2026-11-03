using Beko.Agents.Application.Common;
using Beko.Agents.Application.Options;
using Microsoft.Extensions.Options;

namespace Beko.Agents.Application.Presence;

public static class LocationRoutes
{
    public const string AtHome = "at_home";
    public const string CommuteOut = "commute_out";
    public const string CommuteBack = "commute_back";

    public static readonly IReadOnlyList<string> All = [AtHome, CommuteOut, CommuteBack];
}

/// <summary>Where a member is. <see cref="TimestampUtc"/> is simulator time.</summary>
public sealed record MemberLocation(string MemberId, double Lat, double Lng, DateTime TimestampUtc, string Route);

/// <summary>Port of the location source. The mock implements it today; a real source can replace it later.</summary>
public interface ILocationSource
{
    Task<MemberLocation> GetAsync(string memberId, CancellationToken cancellationToken);
}

/// <summary>Demo control of the mock location.</summary>
public interface IMockLocationControl
{
    /// <summary>Switches a member's scripted route. <paramref name="startDistanceM"/> overrides where the move starts.</summary>
    void SetRoute(string memberId, string route, double? startDistanceM = null);
}

public static class Geo
{
    private const double EarthRadiusM = 6_371_000;

    public static double DistanceM(double lat1, double lng1, double lat2, double lng2)
    {
        var dLat = ToRad(lat2 - lat1);
        var dLng = ToRad(lng2 - lng1);
        var a = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2))
            + (Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2));
        return 2 * EarthRadiusM * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    private static double ToRad(double degrees) => degrees * Math.PI / 180;
}

/// <summary>
/// Scripted routes at the speed of simulated time: <c>at_home</c> stays at the door, <c>commute_out</c> drives away along a fixed bearing
/// until <c>FarDistanceM</c>, <c>commute_back</c> drives back from there (from where the member is now when already away).
/// </summary>
public sealed class MockLocationService(HomeDirectory directory, IHomeClock clock, IOptions<PresenceOptions> options) : ILocationSource, IMockLocationControl
{
    private static readonly Dictionary<string, double> BearingsDeg = new(StringComparer.Ordinal)
    {
        ["ayse"] = 45,
        ["mehmet"] = 135,
        ["zeynep"] = 270,
    };

    private readonly Lock _gate = new();
    private readonly Dictionary<string, RouteState> _states = new(StringComparer.Ordinal);

    public async Task<MemberLocation> GetAsync(string memberId, CancellationToken cancellationToken)
    {
        var home = (await directory.GetAsync(cancellationToken)).Location;
        var now = clock.UtcNow;
        RouteState state;
        double distance;
        lock (_gate)
        {
            state = StateOf(memberId, now);
            distance = DistanceAt(state, now);
        }

        var bearing = (BearingsDeg.TryGetValue(memberId, out var b) ? b : 0) * Math.PI / 180;
        var lat = home.Lat + (distance * Math.Cos(bearing) / 111_320);
        var lng = home.Lng + (distance * Math.Sin(bearing) / (111_320 * Math.Cos(home.Lat * Math.PI / 180)));
        return new MemberLocation(memberId, lat, lng, now, state.Route);
    }

    public void SetRoute(string memberId, string route, double? startDistanceM = null)
    {
        if (!LocationRoutes.All.Contains(route))
        {
            throw new ArgumentException($"Unknown route '{route}'.", nameof(route));
        }

        var now = clock.UtcNow;
        lock (_gate)
        {
            var current = DistanceAt(StateOf(memberId, now), now);
            var start = startDistanceM ?? route switch
            {
                LocationRoutes.AtHome => 0,
                LocationRoutes.CommuteBack => current < 1_000 ? options.Value.FarDistanceM : current,
                _ => current,
            };
            _states[memberId] = new RouteState(route, now, start);
        }
    }

    private RouteState StateOf(string memberId, DateTime now)
    {
        if (!_states.TryGetValue(memberId, out var state))
        {
            state = new RouteState(LocationRoutes.AtHome, now, 0);
            _states[memberId] = state;
        }

        return state;
    }

    private double DistanceAt(RouteState state, DateTime now)
    {
        var metersPerSecond = options.Value.TravelSpeedKmh / 3.6;
        var travelled = Math.Max(0, (now - state.StartedAtUtc).TotalSeconds) * metersPerSecond;
        return state.Route switch
        {
            LocationRoutes.CommuteOut => Math.Min(options.Value.FarDistanceM, state.StartDistanceM + travelled),
            LocationRoutes.CommuteBack => Math.Max(0, state.StartDistanceM - travelled),
            _ => 0,
        };
    }

    private sealed record RouteState(string Route, DateTime StartedAtUtc, double StartDistanceM);
}

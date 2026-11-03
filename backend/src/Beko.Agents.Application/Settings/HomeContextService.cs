using Beko.Agents.Application.Common;
using Beko.Agents.Application.Homes;
using Beko.Agents.Application.Options;
using Beko.Agents.Application.Presence;
using Microsoft.Extensions.Options;

namespace Beko.Agents.Application.Settings;

public sealed record MemberBand(string MemberId, string Band);

/// <summary>Wire shape of <c>GET /api/homes/me/context</c>.</summary>
public sealed record HomeContext(
    double OutdoorTempC,
    double OutdoorHumidityPct,
    string Season,
    DateTime SimulatedNowUtc,
    string PriorityMode,
    IReadOnlyList<MemberBand> Members);

/// <summary>Outdoor weather (fixed mock), season of the simulated date, and who is home.</summary>
public sealed class HomeContextService(
    HomeDirectory directory,
    PresenceStore presence,
    SettingsService settings,
    IHomeClock clock,
    IOptions<SimulationOptions> simulation,
    IOptions<ComfortOptions> comfort)
{
    public double OutdoorTempC => simulation.Value.OutdoorTempC;

    public async Task<HomeContext> GetAsync(CancellationToken cancellationToken)
    {
        var home = await directory.GetAsync(cancellationToken);
        var now = clock.UtcNow;
        return new HomeContext(
            OutdoorTempC,
            comfort.Value.OutdoorHumidityPct,
            SeasonOf(clock.ToLocal(now)),
            now,
            settings.Get(DemoHome.DefaultMemberId).PriorityMode,
            [.. home.Members.Select(m => new MemberBand(m.Id, presence.Band(m.Id)))]);
    }

    /// <summary>Meteorological seasons of the northern hemisphere.</summary>
    public static string SeasonOf(DateTime local) =>
        local.Month switch
        {
            12 or 1 or 2 => "winter",
            >= 3 and <= 5 => "spring",
            >= 6 and <= 8 => "summer",
            _ => "autumn",
        };
}

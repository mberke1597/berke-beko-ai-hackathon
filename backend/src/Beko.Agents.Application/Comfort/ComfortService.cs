using Beko.Agents.Application.Common;
using Beko.Agents.Application.Energy;
using Beko.Agents.Application.Homes;
using Beko.Agents.Application.Learning;
using Beko.Agents.Application.Options;
using Beko.Agents.Application.Presence;
using Beko.Agents.Application.Settings;
using Microsoft.Extensions.Options;

namespace Beko.Agents.Application.Comfort;

/// <summary>Wire shape of <c>GET /api/devices/{id}/comfort</c>.</summary>
public sealed record ComfortResponse(
    string ApplianceId,
    IReadOnlyList<string> Members,
    double RoomTempC,
    double HumidityPct,
    double Pmv,
    string Feeling,
    bool InBand,
    double BandPmv,
    ComfortRecommendation Recommendation,
    ComfortConflict Conflict);

/// <summary>Result of an assessment: the response, or why there is none (<c>not_found</c>, <c>not_air_conditioner</c>, <c>unknown_member</c>).</summary>
public sealed record ComfortOutcome(ComfortResponse? Response, string? Problem);

/// <summary>Collects the live state of an air conditioner and the members, then asks <see cref="ComfortPolicy"/>.</summary>
public sealed class ComfortService(
    IHomeWhizClient homeWhiz,
    HomeContextService context,
    SettingsService settings,
    MemberProfileStore profiles,
    PresenceStore presence,
    HomeDirectory directory,
    EnergyModel energy,
    ComfortPolicy policy,
    IHomeClock clock,
    IOptions<ComfortOptions> options)
{
    /// <summary>Assesses an air conditioner for the given members (default: members currently <c>home</c>).</summary>
    public async Task<ComfortOutcome> AssessAsync(string applianceId, IReadOnlyList<string>? memberIds, CancellationToken cancellationToken)
    {
        var appliance = await homeWhiz.GetApplianceAsync(applianceId, cancellationToken);
        if (appliance is null)
        {
            return new ComfortOutcome(null, "not_found");
        }

        if (appliance.State.Ac is not { } ac || appliance.Capabilities.Setpoint is not { } range)
        {
            return new ComfortOutcome(null, "not_air_conditioner");
        }

        var home = await directory.GetAsync(cancellationToken);
        var ids = memberIds is { Count: > 0 }
            ? memberIds
            : [.. home.Members.Where(m => presence.Band(m.Id) == PresenceBands.Home).Select(m => m.Id)];
        if (ids.Any(id => home.Members.All(m => m.Id != id)))
        {
            return new ComfortOutcome(null, "unknown_member");
        }

        var ctx = await context.GetAsync(cancellationToken);
        var local = clock.ToLocal(clock.UtcNow);
        var members = BuildMembers(ids, local);
        var situation = new ComfortSituation(
            ac.RoomTempC,
            ac.HumidityPct,
            ac.Mode,
            ac.Setpoint,
            ac.Fan,
            appliance.State.Power,
            ctx.OutdoorTempC,
            ctx.OutdoorHumidityPct,
            ctx.Season,
            local.Hour,
            PriorityModes.BandPmv(ctx.PriorityMode),
            members,
            range,
            energy.RateAt(local.TimeOfDay),
            options.Value.DryHumidityThresholdPct);
        var a = policy.Evaluate(situation);
        return new ComfortOutcome(
            new ComfortResponse(applianceId, ids, a.RoomTempC, a.HumidityPct, a.Pmv, a.Feeling, a.InBand, a.BandPmv, a.Recommendation, a.Conflict),
            null);
    }

    /// <summary>
    /// Base PMV (without the member's sensitivity offset) for the member at the appliance's current state; used by the
    /// feedback loop to compare the prediction with what the member felt.
    /// </summary>
    public async Task<(double PmvBase, double Clo, double RoomTempC, double HumidityPct)?> BasePmvAsync(string applianceId, string memberId, CancellationToken cancellationToken)
    {
        var appliance = await homeWhiz.GetApplianceAsync(applianceId, cancellationToken);
        if (appliance?.State.Ac is not { } ac)
        {
            return null;
        }

        var ctx = await context.GetAsync(cancellationToken);
        var local = clock.ToLocal(clock.UtcNow);
        var clo = ComfortEngine.Clo(ctx.Season, settings.Get(memberId).Clothing);
        var pmv = ComfortEngine.Calculate(ac.RoomTempC, ac.HumidityPct, ComfortEngine.AirSpeedMs(ac.Fan, appliance.State.Power), ComfortEngine.MetForHour(local.Hour), clo).Pmv;
        return (pmv, clo, ac.RoomTempC, ac.HumidityPct);
    }

    public static string ContextKey(DateTime local) => $"{HomeTime.DayType(local)}.{HomeTime.Slot(local)}";

    private List<MemberComfort> BuildMembers(IReadOnlyList<string> ids, DateTime local)
    {
        var key = ContextKey(local);
        return
        [
            .. ids.Select(id => new MemberComfort(id, profiles.GetOffset(id), settings.Get(id).Clothing, profiles.GetBias(id, key))),
        ];
    }
}

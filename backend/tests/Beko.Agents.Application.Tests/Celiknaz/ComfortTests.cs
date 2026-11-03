using System.Text.Json;
using Beko.Agents.Application.Common;
using Beko.Agents.Application.Comfort;
using Beko.Agents.Application.Homes;
using Beko.Agents.Application.Learning;
using Beko.Agents.Application.Settings;
using Beko.Agents.Domain.Homes;

namespace Beko.Agents.Application.Tests.Celiknaz;

public sealed class ComfortPolicyTests
{
    private static readonly SetpointRange Range = new(16, 30, 0.5);

    private static readonly string[] FanLevels = ["medium", "high"];

    private readonly ComfortPolicy _policy = new(new Application.Energy.EnergyModel(TestOptions.Energy()));

    private ComfortAssessment Evaluate(
        double room,
        double humidity,
        double band,
        double outdoor = 31,
        string season = "summer",
        int hour = 14,
        params MemberComfort[] members) =>
        _policy.Evaluate(new ComfortSituation(
            room,
            humidity,
            "cool",
            25,
            "auto",
            true,
            outdoor,
            60,
            season,
            hour,
            band,
            members.Length > 0 ? members : [new MemberComfort("ayse", 0, "normal", 0)],
            Range,
            2.6,
            60));

    [Fact]
    public void InsideBand_RecommendsNothing()
    {
        var a = Evaluate(26, 45, band: 1.0);

        Assert.True(a.InBand);
        Assert.Equal(ComfortActions.None, a.Recommendation.Action);
        Assert.Null(a.Recommendation.Setpoint);
    }

    [Fact]
    public void MildOutdoor_WindowIsFreeAndFirst()
    {
        var a = Evaluate(28, 45, band: 0.7, outdoor: 24);

        Assert.False(a.InBand);
        Assert.Equal(ComfortActions.Window, a.Recommendation.Action);
        Assert.Equal(0, a.Recommendation.CostPerHourTl);
        Assert.Contains(a.Recommendation.Alternatives, x => x.Action == ComfortActions.Setpoint);
    }

    [Fact]
    public void HotOutdoor_FanBeatsSetpointOnCost()
    {
        var a = Evaluate(28, 45, band: 0.7, outdoor: 31);

        Assert.Equal(ComfortActions.Fan, a.Recommendation.Action);
        Assert.Equal("fan", a.Recommendation.Mode);
        Assert.Contains(a.Recommendation.Fan, FanLevels);
        var setpoint = Assert.Single(a.Recommendation.Alternatives, x => x.Action == ComfortActions.Setpoint);
        Assert.True(a.Recommendation.CostPerHourTl < setpoint.CostPerHourTl);
    }

    [Fact]
    public void Humid_DryModeIsListedAmongTheCheaperAlternatives()
    {
        var a = Evaluate(28, 70, band: 1.0, outdoor: 31, season: "autumn", hour: 19);

        Assert.False(a.InBand);
        var all = a.Recommendation.Alternatives.Select(x => x.Action).Append(a.Recommendation.Action).ToList();
        Assert.Contains(ComfortActions.Fan, all);
        Assert.Contains(ComfortActions.Dry, all);
        Assert.Contains(ComfortActions.Setpoint, all);
    }

    [Fact]
    public void Setpoint_NarrowerBandGivesLowerCoolingSetpoint_AndLandsInsideTheBand()
    {
        var saving = Evaluate(30, 45, band: 1.0);
        var balanced = Evaluate(30, 45, band: 0.7);
        var comfort = Evaluate(30, 45, band: 0.5);

        Assert.All(new[] { saving, balanced, comfort }, a => Assert.Equal(ComfortActions.Setpoint, a.Recommendation.Action));
        Assert.True(saving.Recommendation.Setpoint > balanced.Recommendation.Setpoint);
        Assert.True(balanced.Recommendation.Setpoint > comfort.Recommendation.Setpoint);
        Assert.True(saving.Recommendation.CostPerHourTl < comfort.Recommendation.CostPerHourTl);

        foreach (var (a, band) in new[] { (saving, 1.0), (balanced, 0.7), (comfort, 0.5) })
        {
            var pmv = ComfortEngine.Calculate(a.Recommendation.Setpoint!.Value, 45, ComfortEngine.AirSpeedMs("auto", true), ComfortEngine.MetForHour(14), ComfortEngine.Clo("summer", "normal")).Pmv;
            Assert.InRange(pmv, -band - 1e-6, band + 1e-6);
        }
    }

    [Fact]
    public void ColdSensitiveMember_GetsWarmerSetpointThanAveragePerson()
    {
        var average = Evaluate(32, 70, band: 0.5);
        var cold = Evaluate(32, 70, band: 0.5, members: new MemberComfort("ayse", 0.8, "normal", 0));

        Assert.Equal(ComfortActions.Setpoint, average.Recommendation.Action);
        Assert.Equal(ComfortActions.Setpoint, cold.Recommendation.Action);
        Assert.True(cold.Recommendation.Setpoint > average.Recommendation.Setpoint);
        Assert.True(cold.Pmv < average.Pmv);
    }

    [Fact]
    public void LearnedBias_ShiftsTheRecommendedSetpoint()
    {
        var plain = Evaluate(30, 45, band: 1.0);
        var biased = Evaluate(30, 45, band: 1.0, members: new MemberComfort("ayse", 0, "normal", 1.0));

        Assert.Equal(plain.Recommendation.Setpoint + 1.0, biased.Recommendation.Setpoint);
    }

    [Fact]
    public void TwoMembers_CompromiseMinimisesTheWorstPmv()
    {
        var ayse = new MemberComfort("ayse", 0.8, "normal", 0);
        var mehmet = new MemberComfort("mehmet", -0.6, "normal", 0);
        var a = Evaluate(30, 45, band: 0.5, members: [ayse, mehmet]);

        Assert.True(a.Conflict.Present);
        var compromise = a.Conflict.CompromiseSetpoint!.Value;
        Assert.Equal(compromise, a.Recommendation.Setpoint);

        double Worst(double t) => new[] { ayse, mehmet }.Max(m =>
            Math.Abs(ComfortEngine.Calculate(t, 45, ComfortEngine.AirSpeedMs("auto", true), ComfortEngine.MetForHour(14), ComfortEngine.Clo("summer", "normal")).Pmv - m.SensitivityOffset));
        for (var t = 16.0; t <= 30; t += 0.5)
        {
            Assert.True(Worst(compromise) <= Worst(t) + 1e-9, $"setpoint {t} is better than the compromise {compromise}");
        }

        Assert.Equal(["ayse", "mehmet"], a.Conflict.PerMember.Select(m => m.MemberId));
        Assert.True(a.Conflict.PerMember[0].PmvAtCompromise < a.Conflict.PerMember[1].PmvAtCompromise + 2);
    }

    [Fact]
    public void TwoSimilarMembers_NoConflict()
    {
        var a = Evaluate(30, 45, band: 1.0, members: [new MemberComfort("ayse", 0.1, "normal", 0), new MemberComfort("mehmet", 0, "normal", 0)]);

        Assert.False(a.Conflict.Present);
    }

    [Fact]
    public void Winter_ColdRoom_RecommendsHeating()
    {
        var a = _policy.Evaluate(new ComfortSituation(17, 45, "heat", 22, "auto", true, 5, 60, "winter", 19, 0.7, [new MemberComfort("ayse", 0, "normal", 0)], Range, 2.6, 60));

        Assert.False(a.InBand);
        Assert.True(a.Pmv < 0);
        Assert.Equal(ComfortActions.Setpoint, a.Recommendation.Action);
        Assert.Equal("heat", a.Recommendation.Mode);
        Assert.InRange(a.Recommendation.Setpoint!.Value, 19, 24);
    }
}

public sealed class SensitivityModelTests
{
    [Fact]
    public void ColdAnswerWhenPredictedNeutral_RaisesOffset_HotLowersIt()
    {
        Assert.True(SensitivityModel.Update(0, 0, -1) > 0);
        Assert.True(SensitivityModel.Update(0, 0, 1) < 0);
    }

    [Fact]
    public void FineAnswerInsideBand_KeepsOffset()
    {
        Assert.Equal(0.2, SensitivityModel.Update(0.2, 0.3, 0), 6);
    }

    [Fact]
    public void RepeatedColdAnswers_ConvergeAndStayBounded()
    {
        double offset = 0;
        for (var i = 0; i < 100; i++)
        {
            offset = SensitivityModel.Update(offset, 0, -1);
        }

        Assert.InRange(offset, 0.45, SensitivityModel.MaxOffset);
    }

    [Fact]
    public void Bias_FollowsAlphaTimesDelta()
    {
        Assert.Equal(0.6, SensitivityModel.UpdateBiasFromOverride(0, 2), 6);
        Assert.Equal(0.3, SensitivityModel.UpdateBias(0, -1), 6);
        Assert.Equal(-0.3, SensitivityModel.UpdateBias(0, 1), 6);
    }
}

public sealed class ComfortServiceTests
{
    [Fact]
    public async Task Assess_UsesMembersAtHome_AndPriorityModeBand()
    {
        var world = new TestWorld(roomTempC: 29);
        world.Presence.Set(new Application.Presence.PresenceEntry("mehmet", "away", 9000, null, world.Clock.UtcNow));

        var saving = await world.Comfort.AssessAsync("ac-salon", null, TestContext.Current.CancellationToken);
        world.Settings.Update("ayse", new MemberSettingsUpdate("comfort", null, null, null, null));
        var comfort = await world.Comfort.AssessAsync("ac-salon", null, TestContext.Current.CancellationToken);

        Assert.Equal(["ayse", "zeynep"], saving.Response!.Members);
        Assert.Equal(1.0, saving.Response.BandPmv);
        Assert.Equal(0.5, comfort.Response!.BandPmv);
        Assert.True(comfort.Response.Recommendation.Setpoint < saving.Response.Recommendation.Setpoint);
    }

    [Fact]
    public async Task Assess_UnknownOrWrongAppliance_ReportsProblem()
    {
        var world = new TestWorld();

        Assert.Equal("not_found", (await world.Comfort.AssessAsync("nope", null, TestContext.Current.CancellationToken)).Problem);
        Assert.Equal("not_air_conditioner", (await world.Comfort.AssessAsync("washer-1", null, TestContext.Current.CancellationToken)).Problem);
        Assert.Equal("unknown_member", (await world.Comfort.AssessAsync("ac-salon", ["nobody"], TestContext.Current.CancellationToken)).Problem);
    }

    [Fact]
    public async Task Feedback_Cold_RaisesOffsetAndBias_AndShiftsTheNextRecommendation()
    {
        var world = new TestWorld(roomTempC: 28);
        var before = await world.Comfort.AssessAsync("ac-salon", ["ayse"], TestContext.Current.CancellationToken);

        var problem = await world.FeedbackService.SubmitAsync("ayse", "ac-salon", -1, "heavy", "cmd-1", TestContext.Current.CancellationToken);
        var after = await world.Comfort.AssessAsync("ac-salon", ["ayse"], TestContext.Current.CancellationToken);

        Assert.Null(problem);
        Assert.True(world.Profiles.GetOffset("ayse") > 0);
        Assert.True(world.Profiles.GetBias("ayse", Application.Comfort.ComfortService.ContextKey(world.Clock.LocalNow())) > 0);
        Assert.Equal("heavy", world.Settings.Get("ayse").Clothing);
        var stored = Assert.Single(await world.Feedback.GetAsync("home-1", DateTime.MinValue, TestContext.Current.CancellationToken));
        Assert.Equal(-1, stored.Sensation);
        Assert.Equal("cmd-1", stored.CommandId);
        Assert.True(after.Response!.Pmv < before.Response!.Pmv);
    }

    [Theory]
    [InlineData(2, "ac-salon", "ayse", "invalid_sensation")]
    [InlineData(0, "ac-salon", "nobody", "unknown_member")]
    [InlineData(0, "nope", "ayse", "not_found")]
    public async Task Feedback_InvalidInput_ReturnsProblemCode(int sensation, string appliance, string member, string expected)
    {
        var world = new TestWorld();

        Assert.Equal(expected, await world.FeedbackService.SubmitAsync(member, appliance, sensation, null, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ComfortCheck_FiresAbout20SimulatedMinutesAfterAnAiCommand_UnlessQuiet()
    {
        var world = new TestWorld();
        var scheduler = new ComfortCheckScheduler(world.Clock, world.Settings, world.Publisher, Microsoft.Extensions.Options.Options.Create(new Application.Options.ComfortOptions()));
        var ct = TestContext.Current.CancellationToken;

        await scheduler.OnCommandAsync(TestEvents.Make("ai", "ayse", "ac-salon", "setpoint", 24, world.Clock.UtcNow), ct);
        await scheduler.OnCommandAsync(TestEvents.Make("app", "mehmet", "ac-salon", "setpoint", 24, world.Clock.UtcNow), ct);
        world.Clock.Advance(TimeSpan.FromMinutes(19));
        await scheduler.ProcessDueAsync(ct);
        Assert.Empty(world.Publisher.ComfortChecks);

        world.Clock.Advance(TimeSpan.FromMinutes(2));
        await scheduler.ProcessDueAsync(ct);
        Assert.Equal(("ac-salon", "ayse"), Assert.Single(world.Publisher.ComfortChecks));

        world.Settings.Update("ayse", new MemberSettingsUpdate(null, "quiet", null, null, null));
        await scheduler.OnCommandAsync(TestEvents.Make("ai", "ayse", "ac-salon", "setpoint", 24, world.Clock.UtcNow), ct);
        world.Clock.Advance(TimeSpan.FromMinutes(30));
        await scheduler.ProcessDueAsync(ct);
        Assert.Single(world.Publisher.ComfortChecks);
        Assert.Equal(0, scheduler.PendingCount);
    }
}

internal static class TestEvents
{
    public static DeviceEventRecord Make(string source, string member, string device, string type, object value, DateTime atUtc, double setpoint = 25, bool power = true)
    {
        var ac = new AcState(setpoint, "cool", "auto", 25, 55);
        var state = new ApplianceState(power, power ? "running" : "off", power ? 820 : 0, atUtc, ac, null, null, null);
        return new DeviceEventRecord(Guid.NewGuid().ToString("N"), "home-1", device, source, member, type, JsonSerializer.SerializeToElement(value), state, state, atUtc, null, null);
    }
}

using Beko.Agents.Application.Common;
using Beko.Agents.Application.Energy;
using Beko.Agents.Application.Notifications;
using Beko.Agents.Application.Options;
using Beko.Agents.Application.Settings;
using Beko.Agents.Domain.Homes;

namespace Beko.Agents.Application.Tests.Celiknaz;

public sealed class EnergyTests
{
    [Fact]
    public void Tariff_BandsByLocalTime_AndMonthlyEstimates()
    {
        var model = new EnergyModel(TestOptions.Energy());

        Assert.Equal(1.6, model.RateAt(TimeSpan.FromHours(23)));
        Assert.Equal(1.6, model.RateAt(TimeSpan.FromHours(3)));
        Assert.Equal(2.6, model.RateAt(TimeSpan.FromHours(12)));
        Assert.Equal(3.9, model.RateAt(TimeSpan.FromHours(18)));
        Assert.Equal("peak", model.BandAt(TimeSpan.FromHours(17)));
        Assert.Equal(380, model.EstimatedMonthlyTl("saving"));
        Assert.Equal(460, model.EstimatedMonthlyTl("balanced"));
        Assert.Equal(590, model.EstimatedMonthlyTl("comfort"));
    }

    [Fact]
    public void Cost_IntegratesAcrossBands()
    {
        var model = new EnergyModel(TestOptions.Energy());
        var date = new DateTime(2026, 11, 3);

        Assert.Equal(0.9 * 3.9, model.CostOf(date.AddHours(18), TimeSpan.FromHours(3), 0.9), 6);
        Assert.Equal(0.9 * 1.6, model.CostOf(date.AddHours(23), TimeSpan.FromHours(3), 0.9), 6);
        var crossing = model.CostOf(date.AddHours(21), TimeSpan.FromHours(2), 1.0);
        Assert.Equal((3.9 + 1.6) / 2, crossing, 2);
    }

    [Fact]
    public void Tariff_FlatWhenTimeOfUseIsOff()
    {
        var options = TestOptions.Energy();
        options.UseTimeOfUse = false;
        var model = new EnergyModel(options);

        Assert.Equal(2.7, model.RateAt(TimeSpan.FromHours(18)));
        Assert.Equal(2.7, model.RateAt(TimeSpan.FromHours(3)));
    }

    [Fact]
    public void AcPower_FollowsModeAndSetpoint()
    {
        var model = new EnergyModel(TestOptions.Energy());

        Assert.Equal(1.0, model.AcKw("cool", 24), 6);
        Assert.Equal(0.86, model.AcKw("cool", 26), 6);
        Assert.Equal(0.6, model.AcKw("dry", 24), 6);
        Assert.Equal(0.12, model.AcKw("fan", 24), 6);
        Assert.True(model.AcKw("heat", 20) < model.AcKw("heat", 22));
    }

    [Fact]
    public void Program_ConfigFirstThenCatalog()
    {
        var model = new EnergyModel(TestOptions.Energy());

        Assert.Equal(0.9, model.FindProgram("dishwasher", "eco50")!.Kwh);
        var catalog = model.FindProgram("washing_machine", "quick30")!;
        Assert.Equal(0.25, catalog.Kwh, 3);
        Assert.Equal(30, catalog.DurationMin);
        Assert.Null(model.FindProgram("dishwasher", "unknown"));
    }

    [Fact]
    public async Task CheapestTime_MovesTheDishwasherIntoTheNightBand()
    {
        var world = new TestWorld();
        var service = new CheapestTimeService(world.Home, world.Model, world.Clock);

        var result = await service.FindAsync("dishwasher-1", null, null, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(new DateTime(2026, 11, 3, 19, 0, 0, DateTimeKind.Utc), result.StartAtUtc);
        Assert.Equal(1.44, result.CostTl);
        Assert.Equal(3.51, result.NowCostTl);
        Assert.Equal(2.07, result.SavingTl);
    }

    [Fact]
    public async Task CheapestTime_RespectsTheDeadline_AndReportsUnknowns()
    {
        var world = new TestWorld();
        var service = new CheapestTimeService(world.Home, world.Model, world.Clock);
        var ct = TestContext.Current.CancellationToken;

        var tight = await service.FindAsync("dishwasher-1", "eco50", world.Clock.UtcNow.AddHours(4), ct);

        Assert.Equal(world.Clock.UtcNow, tight!.StartAtUtc);
        Assert.Equal(0, tight.SavingTl);
        Assert.Null(await service.FindAsync("nope", null, null, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => service.FindAsync("dishwasher-1", "bogus", null, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => service.FindAsync("ac-salon", null, null, ct));
    }

    private static Appliance Ac(FakeHome home, bool power, double setpoint, string mode, double powerW)
    {
        var current = home.GetApplianceAsync("ac-salon", CancellationToken.None).Result!;
        return current with { State = current.State with { Power = power, PowerW = powerW, Ac = current.State.Ac! with { Setpoint = setpoint, Mode = mode } } };
    }

    [Fact]
    public async Task Tracker_AccumulatesKwhAndTl_AndEstimatesSavingsOnlyForAiDecisions()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = new TestWorld();
        var ac = Ac(world.Home, power: true, setpoint: 26, mode: "cool", powerW: 800);

        // Established by the user: no savings attributed.
        await world.Energy.OnCommandAsync(TestEvents.Make("app", "ayse", "ac-salon", "setpoint", 26, world.Clock.UtcNow), ct);
        world.Energy.RecordSample([ac], allAway: false);
        world.Clock.Advance(TimeSpan.FromHours(1));
        world.Energy.RecordSample([ac], allAway: false);
        var byUser = world.Energy.Summary();

        // Established by Çeliknaz: 26 versus the usual 24 saves 14 %.
        await world.Energy.OnCommandAsync(TestEvents.Make("ai", "ayse", "ac-salon", "setpoint", 26, world.Clock.UtcNow), ct);
        world.Clock.Advance(TimeSpan.FromHours(1));
        world.Energy.RecordSample([ac], allAway: false);
        var withAi = world.Energy.Summary();

        Assert.Equal(0.8, byUser.TodayKwh, 2);
        Assert.Equal(0, byUser.SavedWeekKwh);
        Assert.Equal(1.6, withAi.TodayKwh, 2);
        Assert.Equal(0.8 * ((1 / 0.86) - 1), withAi.SavedWeekKwh, 2);
        Assert.True(withAi.SavedWeekTl > 0);
        Assert.True(withAi.TodayTl > 0);
        Assert.Equal("ac-salon", Assert.Single(withAi.PerDevice).ApplianceId);
    }

    [Fact]
    public async Task Tracker_AcSwitchedOffByAiInAnEmptyHome_SavesAwayDuty()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = new TestWorld();
        var off = Ac(world.Home, power: false, setpoint: 25, mode: "cool", powerW: 0);
        await world.Energy.OnCommandAsync(TestEvents.Make("ai", "ayse", "ac-salon", "power", false, world.Clock.UtcNow, power: false), ct);

        world.Energy.RecordSample([off], allAway: true);
        world.Clock.Advance(TimeSpan.FromHours(1));
        world.Energy.RecordSample([off], allAway: true);

        Assert.Equal(0.5, world.Energy.Summary().SavedWeekKwh, 2);
    }

    [Fact]
    public void Tracker_WeekWindowIncludesSeededHistory()
    {
        var world = new TestWorld();
        var today = DateOnly.FromDateTime(world.Clock.LocalNow());
        world.Energy.AddHistory(today.AddDays(-3), "ac-salon", 3, 8, 0.3, 0.8);
        world.Energy.AddHistory(today.AddDays(-10), "ac-salon", 50, 50, 5, 5);

        var summary = world.Energy.Summary();

        Assert.Equal(0, summary.TodayKwh);
        Assert.Equal(3, summary.WeekKwh);
        Assert.Equal(0.3, summary.SavedWeekKwh);
    }
}

public sealed class EnergyOptionsValidationTests
{
    private readonly EnergyOptionsValidator _validator = new();

    [Fact]
    public void Defaults_FromTestOptions_AreValid() => Assert.True(_validator.Validate(null, TestOptions.Energy()).Succeeded);

    [Fact]
    public void BandsWithAGap_Fail()
    {
        var options = TestOptions.Energy();
        options.TimeOfUseBands[0].To = "05:00";

        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Fact]
    public void OverlappingBands_Fail()
    {
        var options = TestOptions.Energy();
        options.TimeOfUseBands[1].From = "05:00";

        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Fact]
    public void MissingModeEstimate_Fails()
    {
        var options = TestOptions.Energy();
        options.EstimatedMonthlyKwh.Remove("comfort");

        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Fact]
    public void BadTime_AndNegativeTariff_Fail()
    {
        var options = TestOptions.Energy();
        options.TimeOfUseBands[0].From = "25:99";
        Assert.True(_validator.Validate(null, options).Failed);

        var negative = TestOptions.Energy();
        negative.TimeOfUseBands[0].TlPerKwh = -1;
        Assert.True(_validator.Validate(null, negative).Failed);
    }

    [Fact]
    public void Flat_NeedsNoBands()
    {
        var options = TestOptions.Energy();
        options.UseTimeOfUse = false;
        options.TimeOfUseBands.Clear();

        Assert.True(_validator.Validate(null, options).Succeeded);
    }
}

public sealed class NotificationTests
{
    private static Task<Notification?> Create(TestWorld world, string level, string kind = "info", string member = "ayse") =>
        world.Notifications.CreateAsync(member, kind, level, "t", "x", null, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Balanced_DeliversCriticalAndImportant_NotInfo()
    {
        var world = new TestWorld();

        Assert.NotNull(await Create(world, "critical"));
        Assert.NotNull(await Create(world, "important"));
        Assert.Null(await Create(world, "info"));
        Assert.Equal(2, world.Publisher.Notifications.Count);
        Assert.Equal(2, world.Notifications.List("ayse").Count);
    }

    [Fact]
    public async Task Quiet_DeliversOnlyCritical_Detailed_DeliversAll()
    {
        var world = new TestWorld();
        world.Settings.Update("ayse", new MemberSettingsUpdate(null, "quiet", null, null, null));
        world.Settings.Update("mehmet", new MemberSettingsUpdate(null, "detailed", null, null, null));

        Assert.Null(await Create(world, "important"));
        Assert.NotNull(await Create(world, "critical"));
        Assert.NotNull(await Create(world, "info", member: "mehmet"));
    }

    [Fact]
    public async Task QuietHours_LetOnlyCriticalThrough()
    {
        var world = new TestWorld();
        world.Clock.UtcNow = new DateTime(2026, 11, 3, 21, 30, 0, DateTimeKind.Utc); // 00:30 home time

        Assert.Null(await Create(world, "important"));
        Assert.NotNull(await Create(world, "critical"));

        world.Clock.UtcNow = new DateTime(2026, 11, 4, 5, 30, 0, DateTimeKind.Utc); // 08:30 home time
        Assert.NotNull(await Create(world, "important"));
    }

    [Fact]
    public async Task Suggestions_AreCappedAtThreePerDay_AndResetTheNextDay()
    {
        var world = new TestWorld();

        for (var i = 0; i < 3; i++)
        {
            Assert.NotNull(await Create(world, "important", "suggestion"));
        }

        Assert.Null(await Create(world, "important", "suggestion"));
        Assert.NotNull(await Create(world, "important", "info"));
        Assert.NotNull(await Create(world, "important", "suggestion", member: "mehmet"));

        world.Clock.Advance(TimeSpan.FromDays(1));
        Assert.NotNull(await Create(world, "important", "suggestion"));
    }

    [Fact]
    public async Task MarkRead_UpdatesTheNotification()
    {
        var world = new TestWorld();
        var created = await Create(world, "critical");

        var read = world.Notifications.MarkRead(created!.Id);

        Assert.True(read!.Read);
        Assert.True(world.Notifications.List("ayse").Single().Read);
        Assert.Null(world.Notifications.MarkRead("nope"));
    }

    [Theory]
    [InlineData("23:00", "08:00", "23:30", true)]
    [InlineData("23:00", "08:00", "07:59", true)]
    [InlineData("23:00", "08:00", "08:00", false)]
    [InlineData("23:00", "08:00", "12:00", false)]
    [InlineData("13:00", "14:00", "13:30", true)]
    public void QuietHours_Contains(string from, string to, string at, bool expected) =>
        Assert.Equal(expected, new QuietHours(from, to).Contains(TimeSpan.Parse(at, System.Globalization.CultureInfo.InvariantCulture)));
}

public sealed class SettingsTests
{
    [Fact]
    public void Defaults_FollowTheContract()
    {
        var world = new TestWorld();

        var view = world.Settings.GetView("ayse");

        Assert.Equal("saving", view.PriorityMode);
        Assert.Equal("balanced", view.NotificationLevel);
        Assert.Equal("normal", view.Clothing);
        Assert.True(view.LocationSharing);
        Assert.Equal(new QuietHours("23:00", "08:00"), view.QuietHours);
        Assert.Equal(0, view.SensitivityOffset);
        Assert.Equal(380, view.EstimatedMonthlyTl["saving"]);
        Assert.Equal(590, view.EstimatedMonthlyTl["comfort"]);
    }

    [Fact]
    public void Update_ChangesOnlyGivenFields_AndRejectsBadValues()
    {
        var world = new TestWorld();

        Assert.Null(world.Settings.Update("ayse", new MemberSettingsUpdate("comfort", null, new QuietHours("22:00", "07:00"), null, false)));
        var s = world.Settings.Get("ayse");
        Assert.Equal("comfort", s.PriorityMode);
        Assert.Equal("balanced", s.NotificationLevel);
        Assert.False(s.LocationSharing);
        Assert.Equal("22:00", s.QuietHours.From);

        Assert.NotNull(world.Settings.Update("ayse", new MemberSettingsUpdate("turbo", null, null, null, null)));
        Assert.NotNull(world.Settings.Update("ayse", new MemberSettingsUpdate(null, "loud", null, null, null)));
        Assert.NotNull(world.Settings.Update("ayse", new MemberSettingsUpdate(null, null, null, "tuxedo", null)));
        Assert.NotNull(world.Settings.Update("ayse", new MemberSettingsUpdate(null, null, new QuietHours("late", "08:00"), null, null)));
        Assert.Equal("comfort", world.Settings.Get("ayse").PriorityMode);
    }

    [Fact]
    public void SeasonFollowsTheSimulatedDate()
    {
        Assert.Equal("winter", HomeContextService.SeasonOf(new DateTime(2026, 1, 5)));
        Assert.Equal("spring", HomeContextService.SeasonOf(new DateTime(2026, 4, 5)));
        Assert.Equal("summer", HomeContextService.SeasonOf(new DateTime(2026, 7, 5)));
        Assert.Equal("autumn", HomeContextService.SeasonOf(new DateTime(2026, 11, 3)));
    }

    [Fact]
    public async Task Context_ReportsOutdoorSeasonModeAndBands()
    {
        var world = new TestWorld();
        world.Presence.Set(new Application.Presence.PresenceEntry("zeynep", "near", 1500, 5, world.Clock.UtcNow));

        var context = await world.Context.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(31, context.OutdoorTempC);
        Assert.Equal("autumn", context.Season);
        Assert.Equal("saving", context.PriorityMode);
        Assert.Equal(world.Clock.UtcNow, context.SimulatedNowUtc);
        Assert.Equal(["home", "home", "near"], context.Members.Select(m => m.Band));
    }
}

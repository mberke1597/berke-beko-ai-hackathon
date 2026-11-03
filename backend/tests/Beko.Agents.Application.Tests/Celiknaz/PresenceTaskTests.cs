using Beko.Agents.Application.Options;
using Beko.Agents.Application.Presence;
using Beko.Agents.Application.Settings;
using Beko.Agents.Application.Tasks;

namespace Beko.Agents.Application.Tests.Celiknaz;

public sealed class GeofenceTests
{
    private static readonly PresenceOptions Options = new();

    [Theory]
    [InlineData("home", 320, "home")]
    [InlineData("home", 360, "near")]
    [InlineData("home", 2100, "away")]
    [InlineData("home", 1500, "near")]
    [InlineData("near", 280, "near")]
    [InlineData("near", 240, "home")]
    [InlineData("near", 2040, "near")]
    [InlineData("near", 2060, "away")]
    [InlineData("away", 1980, "away")]
    [InlineData("away", 1900, "near")]
    [InlineData("away", 100, "home")]
    public void Bands_UseHysteresisAroundTheRadii(string current, double distance, string expected) =>
        Assert.Equal(expected, Geofence.NextBand(current, distance, Options));

    [Fact]
    public void DistanceBetweenCoordinates_IsHaversine()
    {
        Assert.InRange(Geo.DistanceM(41, 29, 42, 29), 111_000, 111_400);
        Assert.Equal(0, Geo.DistanceM(41, 29, 41, 29));
    }
}

public sealed class MockLocationTests
{
    [Fact]
    public async Task CommuteOut_MovesAtTimeScaleSpeed_UntilTheFarPoint()
    {
        var world = new TestWorld();
        var ct = TestContext.Current.CancellationToken;
        var home = (await world.Directory.GetAsync(ct)).Location;

        Assert.Equal("at_home", (await world.Locations.GetAsync("ayse", ct)).Route);
        world.Locations.SetRoute("ayse", LocationRoutes.CommuteOut);
        world.Clock.Advance(TimeSpan.FromMinutes(6));
        var after6 = await world.Locations.GetAsync("ayse", ct);
        world.Clock.Advance(TimeSpan.FromHours(2));
        var far = await world.Locations.GetAsync("ayse", ct);

        Assert.Equal("commute_out", after6.Route);
        Assert.InRange(Geo.DistanceM(home.Lat, home.Lng, after6.Lat, after6.Lng), 3000 - 60, 3000 + 60);
        Assert.InRange(Geo.DistanceM(home.Lat, home.Lng, far.Lat, far.Lng), 10_000 - 60, 10_000 + 60);
        Assert.Equal(world.Clock.UtcNow, far.TimestampUtc);
    }

    [Fact]
    public async Task CommuteBack_StartsFarAndEndsAtHome()
    {
        var world = new TestWorld();
        var ct = TestContext.Current.CancellationToken;
        var home = (await world.Directory.GetAsync(ct)).Location;

        world.Locations.SetRoute("mehmet", LocationRoutes.CommuteBack);
        var start = await world.Locations.GetAsync("mehmet", ct);
        world.Clock.Advance(TimeSpan.FromMinutes(30));
        var end = await world.Locations.GetAsync("mehmet", ct);

        Assert.InRange(Geo.DistanceM(home.Lat, home.Lng, start.Lat, start.Lng), 10_000 - 60, 10_000 + 60);
        Assert.Equal(0, Geo.DistanceM(home.Lat, home.Lng, end.Lat, end.Lng), 0);
    }

    [Fact]
    public void UnknownRoute_Throws()
    {
        var world = new TestWorld();

        Assert.Throws<ArgumentException>(() => world.Locations.SetRoute("ayse", "teleport"));
    }
}

public sealed class PresenceServiceTests
{
    private static readonly string[] Everyone = ["ayse", "mehmet", "zeynep"];

    private static async Task LeaveAllAsync(TestWorld world)
    {
        var ct = TestContext.Current.CancellationToken;
        await world.PresenceService.PollAsync(ct);
        foreach (var member in Everyone)
        {
            world.Locations.SetRoute(member, LocationRoutes.CommuteOut);
        }

        world.Clock.Advance(TimeSpan.FromMinutes(10));
        await world.PresenceService.PollAsync(ct);
    }

    [Fact]
    public async Task Poll_StoresBandsOnly_AndSomeoneHomeKeepsEverythingAsIs()
    {
        var world = new TestWorld();
        var ct = TestContext.Current.CancellationToken;
        await world.PresenceService.PollAsync(ct);
        world.Locations.SetRoute("ayse", LocationRoutes.CommuteOut);
        world.Clock.Advance(TimeSpan.FromMinutes(10));

        await world.PresenceService.PollAsync(ct);
        var all = await world.PresenceService.GetAllAsync(ct);

        Assert.Equal(["away", "home", "home"], all.Select(p => p.Band));
        Assert.InRange(all[0].DistanceM, 5000 - 60, 5000 + 60);
        Assert.Empty(world.Sender.Sent);
    }

    [Fact]
    public async Task EmptyHome_Saving_TurnsTheAcOff_AsAiWithANotification()
    {
        var world = new TestWorld();

        await LeaveAllAsync(world);

        var command = Assert.Single(world.Sender.Sent);
        Assert.Equal(("ac-salon", "power", "ai"), (command.ApplianceId, command.Type, command.Source));
        Assert.False(command.Value.GetBoolean());
        var notification = Assert.Single(world.Publisher.Notifications);
        Assert.Contains("Salon Kliması", notification.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyHome_Balanced_SetsEcoSetback28()
    {
        var world = new TestWorld();
        world.Settings.Update("ayse", new MemberSettingsUpdate("balanced", null, null, null, null));

        await LeaveAllAsync(world);

        var command = Assert.Single(world.Sender.Sent);
        Assert.Equal("setpoint", command.Type);
        Assert.Equal(28, command.Value.GetDouble());
    }

    [Fact]
    public async Task EmptyHome_Comfort_AppliesAMildSetback()
    {
        var world = new TestWorld();
        world.Settings.Update("ayse", new MemberSettingsUpdate("comfort", null, null, null, null));

        await LeaveAllAsync(world);

        var command = Assert.Single(world.Sender.Sent);
        Assert.Equal("setpoint", command.Type);
        Assert.Equal(27, command.Value.GetDouble());
    }

    [Fact]
    public async Task EmptyHome_NoAcRunning_DoesNothing()
    {
        var world = new TestWorld(acOn: false);

        await LeaveAllAsync(world);

        Assert.Empty(world.Sender.Sent);
    }

    [Fact]
    public async Task Approaching_Balanced_PreConditionsOnceWhenEtaIsWithinLeadTime()
    {
        var world = new TestWorld(roomTempC: 29);
        var ct = TestContext.Current.CancellationToken;
        world.Settings.Update("ayse", new MemberSettingsUpdate("balanced", null, null, null, null));
        await LeaveAllAsync(world);
        world.Sender.Sent.Clear();

        world.Locations.SetRoute("ayse", LocationRoutes.CommuteBack);
        await world.PresenceService.PollAsync(ct);
        Assert.Empty(world.Sender.Sent);

        world.Clock.Advance(TimeSpan.FromMinutes(1));
        await world.PresenceService.PollAsync(ct);

        var types = world.Sender.Sent.Select(c => c.Type).ToList();
        Assert.Equal(["power", "mode", "setpoint"], types);
        Assert.All(world.Sender.Sent, c => Assert.Equal("ai", c.Source));
        var entry = (await world.PresenceService.GetAllAsync(ct))[0];
        Assert.InRange(entry.EtaMin!.Value, 5, 20);

        world.Clock.Advance(TimeSpan.FromMinutes(1));
        await world.PresenceService.PollAsync(ct);
        Assert.Equal(3, world.Sender.Sent.Count);
    }

    [Fact]
    public async Task Approaching_Saving_WaitsForTheArrival()
    {
        var world = new TestWorld(roomTempC: 29);
        var ct = TestContext.Current.CancellationToken;
        await LeaveAllAsync(world);
        world.Sender.Sent.Clear();

        world.Locations.SetRoute("ayse", LocationRoutes.CommuteBack);
        await world.PresenceService.PollAsync(ct);
        world.Clock.Advance(TimeSpan.FromMinutes(4));
        await world.PresenceService.PollAsync(ct);
        Assert.Empty(world.Sender.Sent);

        world.Clock.Advance(TimeSpan.FromMinutes(15));
        await world.PresenceService.PollAsync(ct);

        Assert.Equal("home", (await world.PresenceService.GetAllAsync(ct))[0].Band);
        Assert.Equal(["power", "mode", "setpoint"], world.Sender.Sent.Select(c => c.Type));
    }

    [Fact]
    public async Task SpokenLeaving_MovesTheMockRoute_AndAllMembersLeavingTriggersTheSetback()
    {
        var world = new TestWorld();
        var ct = TestContext.Current.CancellationToken;
        await world.PresenceService.PollAsync(ct);

        foreach (var member in Everyone)
        {
            Assert.True(await world.PresenceService.SetSpokenAsync(member, "leaving", ct));
        }

        Assert.Equal("commute_out", (await world.Locations.GetAsync("ayse", ct)).Route);
        Assert.Equal("away", (await world.PresenceService.GetAllAsync(ct))[0].Band);
        Assert.Equal("power", Assert.Single(world.Sender.Sent).Type);

        await world.PresenceService.PollAsync(ct);
        Assert.Equal(["away", "away", "away"], (await world.PresenceService.GetAllAsync(ct)).Select(p => p.Band));
    }

    [Fact]
    public async Task SpokenState_Invalid_IsRejected()
    {
        var world = new TestWorld();

        Assert.False(await world.PresenceService.SetSpokenAsync("ayse", "dancing", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LocationSharingOff_MemberStaysAtTheLastBand()
    {
        var world = new TestWorld();
        var ct = TestContext.Current.CancellationToken;
        world.Settings.Update("ayse", new MemberSettingsUpdate(null, null, null, null, false));
        world.Locations.SetRoute("ayse", LocationRoutes.CommuteOut);
        world.Clock.Advance(TimeSpan.FromMinutes(10));

        await world.PresenceService.PollAsync(ct);

        Assert.Equal("home", (await world.PresenceService.GetAllAsync(ct))[0].Band);
    }
}

public sealed class TaskServiceTests
{
    private static Task Finished(TestWorld world, string applianceId = "washer-1") =>
        world.Tasks.OnApplianceEventAsync("home-1", applianceId, "finished", world.Clock.UtcNow, TestContext.Current.CancellationToken);

    [Fact]
    public void SeededRules_MatchTheContract()
    {
        var world = new TestWorld();

        var rules = world.Tasks.Rules();

        Assert.Equal(3, rules.Count);
        Assert.Contains(rules, r => r.Trigger == "washing_machine.finished" && r.Template == "Çamaşırları as" && r.Strategy == "fair_share" && r.Enabled);
        Assert.Contains(rules, r => r.Trigger == "dishwasher.finished" && r.Template == "Bulaşık makinesini boşalt" && r.Enabled);
        Assert.Contains(rules, r => r.Trigger == "oven.timer_done" && !r.Enabled);
    }

    [Fact]
    public async Task DeviceEvent_CreatesATaskFromTheRule_PublishesAndNotifies()
    {
        var world = new TestWorld();

        await Finished(world);

        var task = Assert.Single(world.Tasks.List("open"));
        Assert.Equal("Çamaşırları as", task.Title);
        Assert.Equal("device_event", task.Origin);
        Assert.Equal("washer-1", task.ApplianceId);
        Assert.Equal(2, task.EffortPoints);
        Assert.StartsWith("Adil dağıtım", task.Reason, StringComparison.Ordinal);
        Assert.Equal(task.Id, Assert.Single(world.Publisher.Tasks).Id);
        Assert.Equal("task", Assert.Single(world.Publisher.Notifications).Kind);
    }

    [Fact]
    public async Task DisabledRule_AndUnknownEvents_CreateNothing()
    {
        var world = new TestWorld();
        await world.Tasks.OnApplianceEventAsync("home-1", "washer-1", "error", world.Clock.UtcNow, TestContext.Current.CancellationToken);
        await world.Tasks.OnApplianceEventAsync("home-1", "nope", "finished", world.Clock.UtcNow, TestContext.Current.CancellationToken);

        Assert.Empty(world.Tasks.List(null));
    }

    [Fact]
    public async Task FairShare_RotatesByLoad_AndTiesGoToTheLeastRecentTask()
    {
        var world = new TestWorld();

        for (var i = 0; i < 4; i++)
        {
            await Finished(world);
            world.Clock.Advance(TimeSpan.FromMinutes(5));
        }

        var assigned = world.Tasks.List(null).OrderBy(t => t.CreatedAtUtc).Select(t => t.AssignedMemberId);
        Assert.Equal(["ayse", "mehmet", "zeynep", "ayse"], assigned);
    }

    [Fact]
    public async Task FairShare_PrefersMembersAtHome_AndCountsPointsDoneInTheLastSevenDays()
    {
        var world = new TestWorld();
        var ct = TestContext.Current.CancellationToken;
        world.Presence.Set(new PresenceEntry("ayse", "away", 5000, null, world.Clock.UtcNow));

        await Finished(world);
        var first = world.Tasks.List(null).Single();
        Assert.Equal("mehmet", first.AssignedMemberId);

        world.Tasks.Complete(first.Id, done: true);
        await Finished(world);
        var second = world.Tasks.List("open").Single();
        Assert.Equal("zeynep", second.AssignedMemberId);

        var stats = await world.Tasks.StatsAsync(ct);
        Assert.Equal(new TaskStat("mehmet", 2, 1), stats.Single(s => s.MemberId == "mehmet"));
        Assert.Equal(new TaskStat("ayse", 0, 0), stats.Single(s => s.MemberId == "ayse"));

        world.Clock.Advance(TimeSpan.FromDays(8));
        Assert.Equal(0, (await world.Tasks.StatsAsync(ct)).Single(s => s.MemberId == "mehmet").PointsLast7Days);
    }

    [Fact]
    public async Task RoundRobin_Fixed_AndAsk_Strategies()
    {
        var world = new TestWorld();
        var ct = TestContext.Current.CancellationToken;

        await world.Tasks.UpdateRuleAsync("rule-washer-finished", new TaskRuleUpdate("round_robin", null, null, null, null), ct);
        for (var i = 0; i < 4; i++)
        {
            await Finished(world);
            world.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(["ayse", "mehmet", "zeynep", "ayse"], world.Tasks.List(null).OrderBy(t => t.CreatedAtUtc).Select(t => t.AssignedMemberId));

        await world.Tasks.UpdateRuleAsync("rule-washer-finished", new TaskRuleUpdate("fixed", "zeynep", null, 3, null), ct);
        await Finished(world);
        var fixedTask = world.Tasks.List(null).OrderBy(t => t.CreatedAtUtc).Last();
        Assert.Equal("zeynep", fixedTask.AssignedMemberId);
        Assert.Equal(3, fixedTask.EffortPoints);
        Assert.Equal("Sabit atama", fixedTask.Reason);

        await world.Tasks.UpdateRuleAsync("rule-washer-finished", new TaskRuleUpdate("ask", null, null, null, null), ct);
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        await Finished(world);
        var asked = world.Tasks.List(null).OrderBy(t => t.CreatedAtUtc).Last();
        Assert.Contains("Soru yanıtsız", asked.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuleUpdate_ValidatesInput()
    {
        var world = new TestWorld();
        var ct = TestContext.Current.CancellationToken;

        Assert.NotNull((await world.Tasks.UpdateRuleAsync("rule-washer-finished", new TaskRuleUpdate("lottery", null, null, null, null), ct)).Error);
        Assert.NotNull((await world.Tasks.UpdateRuleAsync("rule-washer-finished", new TaskRuleUpdate(null, "nobody", null, null, null), ct)).Error);
        Assert.Equal("not_found", (await world.Tasks.UpdateRuleAsync("nope", new TaskRuleUpdate(null, null, null, null, true), ct)).Error);
        var ok = await world.Tasks.UpdateRuleAsync("rule-oven-timer-done", new TaskRuleUpdate(null, null, "Fırını temizle", 4, true), ct);
        Assert.Equal(new TaskRule("rule-oven-timer-done", "oven.timer_done", "Fırını temizle", "fair_share", null, 4, true), ok.Rule);
    }

    [Fact]
    public async Task ManualTask_DefaultsToFairShare_AndRejectsUnknownAssignee()
    {
        var world = new TestWorld();
        var ct = TestContext.Current.CancellationToken;

        var distributed = await world.Tasks.CreateAsync("Market alışverişi", null, 1, "manual", ct);
        var assigned = await world.Tasks.CreateAsync("Banyo temizliği", "zeynep", 3, "voice", ct);

        Assert.Equal("manual", distributed!.Origin);
        Assert.Equal("zeynep", assigned!.AssignedMemberId);
        Assert.Equal("voice", assigned.Origin);
        Assert.Null(await world.Tasks.CreateAsync("x", "nobody", 1, "manual", ct));
    }

    [Fact]
    public async Task Reassign_ToAMember_OrRedistributeWithoutTheCurrentAssignee()
    {
        var world = new TestWorld();
        var ct = TestContext.Current.CancellationToken;
        var task = (await world.Tasks.CreateAsync("Çöpü çıkar", "ayse", 1, "manual", ct))!;

        var handedOver = await world.Tasks.ReassignAsync(task.Id, "mehmet", ct);
        var redistributed = await world.Tasks.ReassignAsync(task.Id, null, ct);

        Assert.Equal("mehmet", handedOver!.AssignedMemberId);
        Assert.Equal("Elle devredildi", handedOver.Reason);
        Assert.NotEqual("mehmet", redistributed!.AssignedMemberId);
        Assert.Null(await world.Tasks.ReassignAsync(task.Id, "nobody", ct));
        Assert.Null(await world.Tasks.ReassignAsync("nope", null, ct));
    }

    [Fact]
    public async Task DoneAndSkip_SetStatus_AndFilterByStatus()
    {
        var world = new TestWorld();
        var ct = TestContext.Current.CancellationToken;
        var a = (await world.Tasks.CreateAsync("a", null, 1, "manual", ct))!;
        var b = (await world.Tasks.CreateAsync("b", null, 1, "manual", ct))!;

        Assert.Equal("done", world.Tasks.Complete(a.Id, done: true)!.Status);
        Assert.Equal("skipped", world.Tasks.Complete(b.Id, done: false)!.Status);
        Assert.Empty(world.Tasks.List("open"));
        Assert.Single(world.Tasks.List("done"));
        Assert.Null(world.Tasks.Complete("nope", done: true));
    }
}

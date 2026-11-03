using Beko.Agents.Application.Comfort;
using Beko.Agents.Application.Common;
using Beko.Agents.Application.Learning;
using Beko.Agents.Application.Settings;

namespace Beko.Agents.Application.Tests.Celiknaz;

public sealed class LearningTests
{
    private static async Task<TestWorld> SeededAsync(int weeks = 5)
    {
        var world = new TestWorld();
        await world.Seeder.SeedAsync(weeks, TestContext.Current.CancellationToken);
        return world;
    }

    [Fact]
    public async Task Seeder_WritesEventsAndFeedback_Deterministically()
    {
        var a = await SeededAsync();
        var b = await SeededAsync();

        Assert.True(a.Events.Records.Count > 100);
        Assert.Equal(a.Events.Records.Count, b.Events.Records.Count);
        Assert.NotEmpty(await a.Feedback.GetAsync("home-1", DateTime.MinValue, TestContext.Current.CancellationToken));
        Assert.Contains(a.Events.Records, e => e.Source == "ai");
        Assert.Contains(a.Events.Records, e => e.Source is "app" or "remote" or "voice");
        Assert.True(a.Energy.Summary().WeekKwh > 10);
    }

    [Fact]
    public async Task Learner_FindsTheRoutinePlantedForAyse()
    {
        var world = await SeededAsync();

        var profile = (await world.Learning.GetProfileAsync("ayse", TestContext.Current.CancellationToken))!;

        var routine = Assert.Single(profile.Routines, r => r.DayType == "weekday" && r.ApplianceId == "ac-salon");
        Assert.Equal("power_on", routine.Action);
        var minutes = TimeSpan.Parse(routine.Time, System.Globalization.CultureInfo.InvariantCulture).TotalMinutes;
        Assert.InRange(minutes, (18 * 60) + 20, (18 * 60) + 40);
        Assert.InRange(routine.Confidence, 0.7, 1);
        Assert.StartsWith("Son 3 haftada", routine.Evidence, StringComparison.Ordinal);
        Assert.Contains("18:", routine.Evidence, StringComparison.Ordinal);
        Assert.Equal(20, routine.WindowMin);
        Assert.Equal("suggested", routine.Status);
        Assert.DoesNotContain(profile.Routines, r => r.DayType == "weekend");
    }

    [Fact]
    public async Task Learner_FindsMehmetsEveningRoutine_AndBothPreferredSetpoints()
    {
        var world = await SeededAsync();
        var ct = TestContext.Current.CancellationToken;

        var mehmet = (await world.Learning.GetProfileAsync("mehmet", ct))!;
        var ayse = (await world.Learning.GetProfileAsync("ayse", ct))!;

        var routine = Assert.Single(mehmet.Routines, r => r.DayType == "weekday");
        var minutes = TimeSpan.Parse(routine.Time, System.Globalization.CultureInfo.InvariantCulture).TotalMinutes;
        Assert.InRange(minutes, (19 * 60) + 45, (20 * 60) + 15);

        var night = Assert.Single(ayse.Preferences, p => p.Context == "weekday night, hot");
        Assert.Equal(26, night.Setpoint);
        Assert.True(night.Confidence > 0.7);
        Assert.Contains("26", night.Text, StringComparison.Ordinal);
        Assert.Contains(mehmet.Preferences, p => p.Setpoint == 23 && p.Context == "weekday evening, hot");
    }

    [Fact]
    public async Task OverrideRateFalls_AndGoodFeedbackRises_OverTheSeededWeeks()
    {
        var world = await SeededAsync();
        var profile = (await world.Learning.GetProfileAsync("ayse", TestContext.Current.CancellationToken))!;

        Assert.True(profile.OverrideRate.Count >= 5);
        Assert.Equal(profile.OverrideRate.OrderBy(w => w.WeekStartUtc), profile.OverrideRate);
        Assert.All(profile.OverrideRate, w => Assert.Equal(DayOfWeek.Sunday, w.WeekStartUtc.AddHours(3).AddDays(-1).DayOfWeek));
        var firstHalf = profile.OverrideRate.Take(2).Average(w => w.Rate);
        var lastHalf = profile.OverrideRate.TakeLast(2).Average(w => w.Rate);
        Assert.True(firstHalf - lastHalf >= 0.3, $"override rate went {firstHalf} -> {lastHalf}");

        var goodFirst = profile.GoodFeedbackRate.Take(2).Average(w => w.Rate);
        var goodLast = profile.GoodFeedbackRate.TakeLast(2).Average(w => w.Rate);
        Assert.True(goodLast - goodFirst >= 0.2, $"good feedback went {goodFirst} -> {goodLast}");
    }

    [Fact]
    public async Task Sensitivity_AyseGetsColdEasily_MehmetDoesNot()
    {
        var world = await SeededAsync();

        var ayse = world.Profiles.GetOffset("ayse");
        var mehmet = world.Profiles.GetOffset("mehmet");

        Assert.True(ayse > 0.2, $"ayse offset {ayse}");
        Assert.True(mehmet < ayse);
        Assert.True(world.Profiles.GetBias("ayse", "weekday.evening") > 0);
    }

    [Fact]
    public async Task Learner_IsIdempotent_AndSuggestsEachRoutineOnce()
    {
        var world = await SeededAsync();
        var ct = TestContext.Current.CancellationToken;
        var before = world.Profiles.GetView("ayse");

        await world.Learner.RunAllAsync(ct);
        var after = world.Profiles.GetView("ayse");

        Assert.Equal(before.SensitivityOffset, after.SensitivityOffset);
        Assert.Equal(before.Routines, after.Routines);
        Assert.Equal(1, world.Notifications.List("ayse").Count(n => n.Action?.Type == "accept_routine"));
    }

    [Fact]
    public async Task Reset_ForgetsEverything_AndOlderHistoryIsIgnoredAfterwards()
    {
        var world = await SeededAsync();
        var ct = TestContext.Current.CancellationToken;

        Assert.True(await world.Learning.ResetAsync("ayse", ct));
        await world.Learner.RunAsync("ayse", ct);
        var profile = world.Profiles.GetView("ayse");

        Assert.Equal(0, profile.SensitivityOffset);
        Assert.Empty(profile.Routines);
        Assert.Empty(profile.Preferences);
        Assert.Empty(profile.OverrideRate);
        Assert.NotEmpty(world.Profiles.GetView("mehmet").Routines);
        Assert.False(await world.Learning.ResetAsync("nobody", ct));
    }

    [Fact]
    public async Task AcceptAndReject_ChangeStatus_AndRejectedRoutinesAreNotSuggestedAgain()
    {
        var world = await SeededAsync();
        var ct = TestContext.Current.CancellationToken;
        var routineId = world.Profiles.GetView("ayse").Routines.Single().Id;
        var mehmetId = world.Profiles.GetView("mehmet").Routines.Single(r => r.DayType == "weekday").Id;

        Assert.Equal("accepted", world.Learning.Accept(routineId)!.Status);
        Assert.Equal("rejected", world.Learning.Reject(mehmetId)!.Status);
        await world.Learner.RunAllAsync(ct);

        Assert.Equal("accepted", world.Profiles.FindRoutine(routineId)!.Status);
        Assert.Equal("rejected", world.Profiles.FindRoutine(mehmetId)!.Status);
        Assert.Null(world.Learning.Accept("nope"));
    }

    [Fact]
    public async Task AcceptedRoutine_BecomesAScheduleThatFiresOncePerDay()
    {
        var world = await SeededAsync();
        var ct = TestContext.Current.CancellationToken;
        var routine = world.Profiles.GetView("ayse").Routines.Single();
        world.Learning.Accept(routine.Id);
        var scheduler = new RoutineScheduler(world.Profiles, world.Sender, world.Clock, world.Notifications);

        world.Clock.UtcNow = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc); // 15:00 home time, Tuesday
        await scheduler.ProcessDueAsync(ct);
        Assert.Empty(world.Sender.Sent);

        world.Clock.UtcNow = new DateTime(2026, 11, 3, 15, 45, 0, DateTimeKind.Utc); // 18:45
        await scheduler.ProcessDueAsync(ct);
        await scheduler.ProcessDueAsync(ct);
        var sent = Assert.Single(world.Sender.Sent);
        Assert.Equal(("ac-salon", "power", "schedule", "ayse"), (sent.ApplianceId, sent.Type, sent.Source, sent.MemberId));
        Assert.True(sent.Value.GetBoolean());

        world.Clock.UtcNow = new DateTime(2026, 11, 7, 15, 45, 0, DateTimeKind.Utc); // Saturday: weekday routine does not run
        await scheduler.ProcessDueAsync(ct);
        Assert.Single(world.Sender.Sent);
    }

    [Fact]
    public async Task Override_IsDetectedFromAnAiCommandFollowedByAUserCommand()
    {
        var world = new TestWorld();
        var ct = TestContext.Current.CancellationToken;
        var t = world.Clock.UtcNow.AddMinutes(-5);
        await world.Events.AddAsync(TestEvents.Make("ai", "ayse", "ac-salon", "setpoint", 24, t, setpoint: 24), ct);
        await world.Events.AddAsync(TestEvents.Make("remote", "ayse", "ac-salon", "setpoint", 26, t.AddSeconds(10), setpoint: 26), ct);
        await world.Events.AddAsync(TestEvents.Make("ai", "ayse", "ac-salon", "setpoint", 26, t.AddMinutes(2), setpoint: 26), ct);

        await world.Learner.RunAsync("ayse", ct);

        var profile = world.Profiles.GetView("ayse");
        Assert.Equal(0.5, Assert.Single(profile.OverrideRate).Rate);
        Assert.Equal(0.6, world.Profiles.GetBias("ayse", Application.Comfort.ComfortService.ContextKey(world.Clock.ToLocal(t))), 6);
    }

    [Fact]
    public async Task Override_TooLateIsNotAnOverride()
    {
        var world = new TestWorld();
        var ct = TestContext.Current.CancellationToken;
        var t = world.Clock.UtcNow.AddMinutes(-30);
        await world.Events.AddAsync(TestEvents.Make("ai", "ayse", "ac-salon", "setpoint", 24, t, setpoint: 24), ct);
        await world.Events.AddAsync(TestEvents.Make("remote", "ayse", "ac-salon", "setpoint", 26, t.AddMinutes(5), setpoint: 26), ct);

        await world.Learner.RunAsync("ayse", ct);

        Assert.Equal(0, Assert.Single(world.Profiles.GetView("ayse").OverrideRate).Rate);
    }

    [Fact]
    public async Task OverrideDetector_RunsTheLearnerRightAway()
    {
        var world = new TestWorld();
        var ct = TestContext.Current.CancellationToken;
        var detector = new OverrideDetector(world.Clock, world.Learner, world.Time);
        var ai = TestEvents.Make("ai", "ayse", "ac-salon", "setpoint", 24, world.Clock.UtcNow, setpoint: 24);
        await world.Events.AddAsync(ai, ct);
        await detector.OnCommandAsync(ai, ct);
        world.Clock.Advance(TimeSpan.FromSeconds(10));
        var user = TestEvents.Make("remote", "ayse", "ac-salon", "setpoint", 26, world.Clock.UtcNow, setpoint: 26);
        await world.Events.AddAsync(user, ct);

        await detector.OnCommandAsync(user, ct);

        Assert.Equal(1, Assert.Single(world.Profiles.GetView("ayse").OverrideRate).Rate);
    }

    [Fact]
    public async Task ProposeAutomation_StoresASuggestionWithAnAcceptAction()
    {
        var world = new TestWorld();
        var ct = TestContext.Current.CancellationToken;

        var routine = await world.Learning.ProposeAsync("ayse", "ac-salon", "weekday", "18:30", "Akşamları serin olsun", ct);

        Assert.Equal("suggested", routine!.Status);
        var notification = Assert.Single(world.Notifications.List("ayse"));
        Assert.Equal(routine.Id, notification.Action!.RoutineId);
        Assert.Null(await world.Learning.ProposeAsync("ayse", "ac-salon", "weekday", "late", "x", ct));
        Assert.Null(await world.Learning.ProposeAsync("nobody", "ac-salon", "weekday", "18:30", "x", ct));
    }

    [Fact]
    public async Task Facade_WrapsTheServices()
    {
        var world = await SeededAsync();
        var ct = TestContext.Current.CancellationToken;
        var cheapest = new Application.Energy.CheapestTimeService(world.Home, world.Model, world.Clock);
        var facade = new CeliknazHomeFacade(world.Comfort, world.FeedbackService, world.Learning, world.PresenceService, world.Settings, world.Directory, cheapest, world.Model, world.Home, world.Clock, world.Tasks);

        Assert.True((await facade.GetComfortAssessmentAsync("ac-salon", null, ct)).Ok);
        Assert.False((await facade.GetComfortAssessmentAsync("washer-1", null, ct)).Ok);
        Assert.True((await facade.ReportThermalSensationAsync("ayse", "ac-salon", -1, null, null, ct)).Ok);
        Assert.Equal("sensation must be -1, 0 or 1.", (await facade.ReportThermalSensationAsync("ayse", "ac-salon", 5, null, null, ct)).Error);
        Assert.NotEmpty((await facade.GetMemberProfileAsync("ayse", ct)).Value!.Routines);
        Assert.True((await facade.UpdateSettingsAsync("ayse", new MemberSettingsUpdate("balanced", null, null, null, null), ct)).Ok);
        Assert.Equal("balanced", (await facade.GetSettingsAsync("ayse", ct)).Value!.PriorityMode);
        Assert.False((await facade.UpdateSettingsAsync("ayse", new MemberSettingsUpdate("x", null, null, null, null), ct)).Ok);
        Assert.True((await facade.FindCheapestTimeAsync("dishwasher-1", null, null, ct)).Value!.SavingTl > 0);
        Assert.False((await facade.FindCheapestTimeAsync("ac-salon", null, null, ct)).Ok);
        Assert.Equal("ac_hours", (await facade.EstimateEnergyAsync("ac-salon", "cool", 24, 2, null, ct)).Value!.Basis);
        Assert.Equal("program_run", (await facade.EstimateEnergyAsync("dishwasher-1", null, null, 1, null, ct)).Value!.Basis);
        var task = (await facade.CreateTaskAsync("Market", null, 1, ct)).Value!;
        Assert.Equal("mehmet", (await facade.AssignTaskAsync(task.Id, "mehmet", ct)).Value!.AssignedMemberId);
        Assert.Single(await facade.ListTasksAsync("open", ct));
        Assert.True((await facade.ProposeAutomationAsync("ayse", "ac-salon", "weekend", "13:00", "test", ct)).Ok);
        Assert.True((await facade.SetPresenceAsync("ayse", "leaving", ct)).Ok);
        Assert.False((await facade.SetPresenceAsync("ayse", "flying", ct)).Ok);
    }
}

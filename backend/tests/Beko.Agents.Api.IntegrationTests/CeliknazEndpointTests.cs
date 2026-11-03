using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Beko.Agents.Application.Dashboard;
using Beko.Agents.Application.Homes;
using Beko.Agents.Application.Learning;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Beko.Agents.Api.IntegrationTests;

internal static class CeliknazTestEnvironment
{
    /// <summary>Background jobs (presence, energy sampling, learner) off, so tests drive the services themselves.</summary>
    [ModuleInitializer]
    internal static void DisableBackgroundJobs() =>
        Environment.SetEnvironmentVariable("CeliknazHome__RunBackgroundJobs", "false");
}

/// <summary>Home test host with the slice 2 Mongo-backed ports replaced by memory.</summary>
public sealed class CeliknazApiFactory : HomeApiFactory
{
    public InMemoryDeviceEventLog Log { get; } = new();

    public InMemoryComfortFeedbackStore Feedback { get; } = new();

    public FakeRunStats RunStats { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDeviceEventStore>();
            services.AddSingleton<IDeviceEventStore>(Log);
            services.RemoveAll<IDeviceEventReader>();
            services.AddSingleton<IDeviceEventReader>(Log);
            services.RemoveAll<IComfortFeedbackStore>();
            services.AddSingleton<IComfortFeedbackStore>(Feedback);
            services.RemoveAll<IRunStatsReader>();
            services.AddSingleton<IRunStatsReader>(RunStats);
        });
    }
}

public sealed class FakeRunStats : IRunStatsReader
{
    public RunStats Stats { get; set; } = RunStats.Empty;

    public Task<RunStats> GetAsync(DateTime todayStartUtc, DateTime monthStartUtc, CancellationToken cancellationToken) => Task.FromResult(Stats);
}

public sealed class CeliknazEndpointTests(CeliknazApiFactory factory) : IClassFixture<CeliknazApiFactory>
{
    private static readonly string[] Seasons = ["winter", "spring", "summer", "autumn"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonDocument> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative), Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Settings_DefaultsAndUpdate_ReturnTheUpdatedObject()
    {
        using var client = factory.CreateClient();

        using var initial = await GetAsync(client, "/api/me/settings?memberId=mehmet");
        Assert.Equal("saving", initial.RootElement.GetProperty("priorityMode").GetString());
        Assert.Equal("balanced", initial.RootElement.GetProperty("notificationLevel").GetString());
        Assert.Equal("23:00", initial.RootElement.GetProperty("quietHours").GetProperty("from").GetString());
        Assert.Equal(460, initial.RootElement.GetProperty("estimatedMonthlyTl").GetProperty("balanced").GetDouble());

        using var put = await client.PutAsJsonAsync(
            new Uri("/api/me/settings?memberId=mehmet", UriKind.Relative),
            new { priorityMode = "comfort", clothing = "heavy", sensitivityOffset = 9 },
            Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        using var updated = JsonDocument.Parse(await put.Content.ReadAsStringAsync(Ct));
        Assert.Equal("comfort", updated.RootElement.GetProperty("priorityMode").GetString());
        Assert.Equal("heavy", updated.RootElement.GetProperty("clothing").GetString());
        Assert.Equal(0, updated.RootElement.GetProperty("sensitivityOffset").GetDouble());

        using var bad = await client.PutAsJsonAsync(new Uri("/api/me/settings?memberId=mehmet", UriKind.Relative), new { priorityMode = "turbo" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var unknown = await client.GetAsync(new Uri("/api/me/settings?memberId=nobody", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Context_HasOutdoorSeasonAndMemberBands()
    {
        using var client = factory.CreateClient();

        using var json = await GetAsync(client, "/api/homes/me/context");

        var root = json.RootElement;
        Assert.Equal(31, root.GetProperty("outdoorTempC").GetDouble());
        Assert.Contains(root.GetProperty("season").GetString(), Seasons);
        Assert.Equal(3, root.GetProperty("members").GetArrayLength());
        Assert.Equal("home", root.GetProperty("members")[0].GetProperty("band").GetString());
        Assert.True(root.TryGetProperty("simulatedNowUtc", out _));
    }

    [Fact]
    public async Task Comfort_ReturnsPmvRecommendationAndConflictShape()
    {
        using var client = factory.CreateClient();

        using var json = await GetAsync(client, "/api/devices/ac-salon/comfort?memberIds=ayse,mehmet");

        var root = json.RootElement;
        Assert.Equal("ac-salon", root.GetProperty("applianceId").GetString());
        Assert.Equal(["ayse", "mehmet"], root.GetProperty("members").EnumerateArray().Select(m => m.GetString()));
        Assert.True(root.GetProperty("pmv").ValueKind == JsonValueKind.Number);
        Assert.False(string.IsNullOrEmpty(root.GetProperty("feeling").GetString()));
        Assert.Equal(1.0, root.GetProperty("bandPmv").GetDouble());
        var recommendation = root.GetProperty("recommendation");
        Assert.False(string.IsNullOrEmpty(recommendation.GetProperty("action").GetString()));
        Assert.False(string.IsNullOrEmpty(recommendation.GetProperty("reason").GetString()));
        Assert.True(recommendation.GetProperty("alternatives").ValueKind == JsonValueKind.Array);
        Assert.Equal(2, root.GetProperty("conflict").GetProperty("perMember").GetArrayLength());

        using var wrong = await client.GetAsync(new Uri("/api/devices/washer-1/comfort", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrong.StatusCode);
        using var missing = await client.GetAsync(new Uri("/api/devices/nope/comfort", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task ComfortFeedback_Returns204_AndMovesTheSensitivityOffset()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/me/comfort-feedback?memberId=zeynep", UriKind.Relative),
            new { applianceId = "ac-salon", sensation = -1, clothing = "heavy", commandId = "c1" },
            Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var settings = await GetAsync(client, "/api/me/settings?memberId=zeynep");
        Assert.True(settings.RootElement.GetProperty("sensitivityOffset").GetDouble() > 0);
        Assert.Equal("heavy", settings.RootElement.GetProperty("clothing").GetString());
        Assert.Contains(await factory.Feedback.GetAsync("home-1", DateTime.MinValue, Ct), f => f.MemberId == "zeynep" && f.CommandId == "c1");

        using var bad = await client.PostAsJsonAsync(new Uri("/api/me/comfort-feedback", UriKind.Relative), new { applianceId = "ac-salon", sensation = 7 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Energy_AndCheapestTime_HaveTheContractShape()
    {
        using var client = factory.CreateClient();

        using var energy = await GetAsync(client, "/api/homes/me/energy");
        foreach (var name in new[] { "todayKwh", "todayTl", "weekKwh", "weekTl", "savedWeekKwh", "savedWeekTl" })
        {
            Assert.Equal(JsonValueKind.Number, energy.RootElement.GetProperty(name).ValueKind);
        }

        Assert.Equal(JsonValueKind.Array, energy.RootElement.GetProperty("perDevice").ValueKind);

        using var cheapest = await GetAsync(client, "/api/homes/me/cheapest-time?applianceId=dishwasher-1&programId=eco50");
        Assert.True(cheapest.RootElement.GetProperty("savingTl").GetDouble() >= 0);
        Assert.True(cheapest.RootElement.GetProperty("nowCostTl").GetDouble() >= cheapest.RootElement.GetProperty("costTl").GetDouble());
        Assert.True(cheapest.RootElement.GetProperty("startAtUtc").GetDateTime() > DateTime.UtcNow.AddMinutes(-5));

        using var bad = await client.GetAsync(new Uri("/api/homes/me/cheapest-time?applianceId=dishwasher-1&programId=bogus", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var missing = await client.GetAsync(new Uri("/api/homes/me/cheapest-time?applianceId=nope", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task MockLocation_RoutesAndPresence()
    {
        using var client = factory.CreateClient();

        using var home = await GetAsync(client, "/api/mock/location?memberId=zeynep");
        Assert.Equal("at_home", home.RootElement.GetProperty("route").GetString());

        using var put = await client.PutAsJsonAsync(new Uri("/api/mock/location/zeynep/route", UriKind.Relative), new { route = "commute_back" }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        using var back = await GetAsync(client, "/api/mock/location?memberId=zeynep");
        Assert.Equal("commute_back", back.RootElement.GetProperty("route").GetString());
        Assert.NotEqual(home.RootElement.GetProperty("lng").GetDouble(), back.RootElement.GetProperty("lng").GetDouble());

        using var invalid = await client.PutAsJsonAsync(new Uri("/api/mock/location/zeynep/route", UriKind.Relative), new { route = "teleport" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var presence = await GetAsync(client, "/api/homes/me/presence");
        Assert.Equal(3, presence.RootElement.GetArrayLength());
        Assert.Equal("ayse", presence.RootElement[0].GetProperty("memberId").GetString());
        Assert.True(presence.RootElement[0].TryGetProperty("etaMin", out _));
        Assert.True(presence.RootElement[0].TryGetProperty("updatedAtUtc", out _));

        using var spoken = await client.PostAsJsonAsync(new Uri("/api/me/presence?memberId=zeynep", UriKind.Relative), new { state = "leaving" }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, spoken.StatusCode);
        using var after = await GetAsync(client, "/api/homes/me/presence");
        Assert.Equal("away", after.RootElement[2].GetProperty("band").GetString());
        using var badState = await client.PostAsJsonAsync(new Uri("/api/me/presence", UriKind.Relative), new { state = "dancing" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badState.StatusCode);
    }

    [Fact]
    public async Task Tasks_CreateDistributeCompleteReassignAndStats()
    {
        using var client = factory.CreateClient();

        using var created = await client.PostAsJsonAsync(new Uri("/api/homes/me/tasks", UriKind.Relative), new { title = "Market alışverişi", assignedMemberId = (string?)null, effortPoints = 3 }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var task = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        var id = task.RootElement.GetProperty("id").GetString();
        Assert.Equal("manual", task.RootElement.GetProperty("origin").GetString());
        Assert.Equal("open", task.RootElement.GetProperty("status").GetString());
        Assert.Equal(3, task.RootElement.GetProperty("effortPoints").GetInt32());
        Assert.False(string.IsNullOrEmpty(task.RootElement.GetProperty("assignedMemberId").GetString()));
        Assert.Equal(JsonValueKind.Null, task.RootElement.GetProperty("dueAtUtc").ValueKind);

        using var reassigned = await client.PostAsJsonAsync(new Uri($"/api/tasks/{id}/reassign", UriKind.Relative), new { memberId = "zeynep" }, Ct);
        using var reassignedJson = JsonDocument.Parse(await reassigned.Content.ReadAsStringAsync(Ct));
        Assert.Equal("zeynep", reassignedJson.RootElement.GetProperty("assignedMemberId").GetString());

        using var open = await GetAsync(client, "/api/homes/me/tasks?status=open");
        Assert.Contains(open.RootElement.EnumerateArray(), t => t.GetProperty("id").GetString() == id);

        using var done = await client.PostAsync(new Uri($"/api/tasks/{id}/done", UriKind.Relative), content: null, Ct);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        using var doneJson = JsonDocument.Parse(await done.Content.ReadAsStringAsync(Ct));
        Assert.Equal("done", doneJson.RootElement.GetProperty("status").GetString());
        using var stats = await GetAsync(client, "/api/homes/me/task-stats");
        Assert.True(stats.RootElement.EnumerateArray().Single(s => s.GetProperty("memberId").GetString() == "zeynep").GetProperty("pointsLast7Days").GetInt32() >= 3);

        using var skip = await client.PostAsJsonAsync(new Uri($"/api/tasks/{id}/skip", UriKind.Relative), new { }, Ct);
        Assert.Equal(HttpStatusCode.OK, skip.StatusCode);
        using var missing = await client.PostAsync(new Uri("/api/tasks/nope/done", UriKind.Relative), content: null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task TaskRules_AreSeeded_AndPutReturnsTheUpdatedRule()
    {
        using var client = factory.CreateClient();

        using var rules = await GetAsync(client, "/api/homes/me/task-rules");
        Assert.Equal(3, rules.RootElement.GetArrayLength());
        var oven = rules.RootElement.EnumerateArray().Single(r => r.GetProperty("trigger").GetString() == "oven.timer_done");
        Assert.False(oven.GetProperty("enabled").GetBoolean());

        using var put = await client.PutAsJsonAsync(
            new Uri($"/api/homes/me/task-rules/{oven.GetProperty("id").GetString()}", UriKind.Relative),
            new { strategy = "fixed", fixedMemberId = "mehmet", enabled = true, effortPoints = 2 },
            Ct);
        using var updated = JsonDocument.Parse(await put.Content.ReadAsStringAsync(Ct));
        Assert.Equal("fixed", updated.RootElement.GetProperty("strategy").GetString());
        Assert.Equal("mehmet", updated.RootElement.GetProperty("fixedMemberId").GetString());
        Assert.True(updated.RootElement.GetProperty("enabled").GetBoolean());

        using var bad = await client.PutAsJsonAsync(new Uri("/api/homes/me/task-rules/rule-oven-timer-done", UriKind.Relative), new { strategy = "lottery" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task SeedHistory_FillsTheProfile_AndNotificationsCarryTheSuggestion()
    {
        using var client = factory.CreateClient();

        using var seed = await client.PostAsJsonAsync(new Uri("/api/demo/seed-history", UriKind.Relative), new { memberId = "ayse", weeks = 5 }, Ct);
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);
        using var seedJson = JsonDocument.Parse(await seed.Content.ReadAsStringAsync(Ct));
        Assert.True(seedJson.RootElement.GetProperty("events").GetInt32() > 100);

        using var profile = await GetAsync(client, "/api/me/profile?memberId=ayse");
        var routine = profile.RootElement.GetProperty("routines").EnumerateArray().Single(r => r.GetProperty("dayType").GetString() == "weekday");
        Assert.Equal("ac-salon", routine.GetProperty("applianceId").GetString());
        Assert.StartsWith("18:", routine.GetProperty("time").GetString(), StringComparison.Ordinal);
        Assert.True(profile.RootElement.GetProperty("overrideRate").GetArrayLength() >= 5);
        Assert.True(profile.RootElement.GetProperty("sensitivityOffset").GetDouble() > 0);
        Assert.NotEqual(0, profile.RootElement.GetProperty("preferences").GetArrayLength());

        using var notifications = await GetAsync(client, "/api/me/notifications?memberId=ayse");
        var suggestion = notifications.RootElement.EnumerateArray().First(n => n.GetProperty("kind").GetString() == "suggestion");
        Assert.Equal("accept_routine", suggestion.GetProperty("action").GetProperty("type").GetString());
        Assert.Equal(routine.GetProperty("id").GetString(), suggestion.GetProperty("action").GetProperty("routineId").GetString());

        using var read = await client.PostAsync(new Uri($"/api/me/notifications/{suggestion.GetProperty("id").GetString()}/read", UriKind.Relative), content: null, Ct);
        using var readJson = JsonDocument.Parse(await read.Content.ReadAsStringAsync(Ct));
        Assert.True(readJson.RootElement.GetProperty("read").GetBoolean());

        using var accept = await client.PostAsync(new Uri($"/api/me/routines/{routine.GetProperty("id").GetString()}/accept", UriKind.Relative), content: null, Ct);
        using var acceptJson = JsonDocument.Parse(await accept.Content.ReadAsStringAsync(Ct));
        Assert.Equal("accepted", acceptJson.RootElement.GetProperty("status").GetString());

        using var reset = await client.DeleteAsync(new Uri("/api/me/learning?memberId=ayse", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        using var cleared = await GetAsync(client, "/api/me/profile?memberId=ayse");
        Assert.Equal(0, cleared.RootElement.GetProperty("routines").GetArrayLength());
        Assert.Equal(0, cleared.RootElement.GetProperty("sensitivityOffset").GetDouble());
    }

    [Fact]
    public async Task Dashboard_SummaryAndProjection_UseRunStatsAndInfraOptions()
    {
        factory.RunStats.Stats = new RunStats(4, 2, 0.12, 1.5, 9000, 0.05, 1800, [new ToolStat("control_device", 7, 1)]);
        using var client = factory.CreateClient();
        using var command = await client.PostAsJsonAsync(
            new Uri("/api/devices/ac-yatak/commands", UriKind.Relative),
            new { type = "power", value = true, source = "voice", memberId = "ayse" },
            Ct);
        Assert.Equal(HttpStatusCode.Accepted, command.StatusCode);

        using var summary = await GetAsync(client, "/api/dashboard/summary");

        var root = summary.RootElement;
        Assert.Equal(1, root.GetProperty("homes").GetInt32());
        Assert.True(root.GetProperty("commandsToday").GetProperty("voice").GetInt32() >= 1);
        Assert.Equal(4, root.GetProperty("runsToday").GetInt64());
        Assert.Equal(2, root.GetProperty("conversationsToday").GetInt64());
        Assert.Equal(1.5, root.GetProperty("llm").GetProperty("costUsdMonth").GetDouble());
        Assert.Equal(1800, root.GetProperty("llm").GetProperty("avgLatencyMs").GetDouble());
        Assert.Equal("control_device", root.GetProperty("tools")[0].GetProperty("tool").GetString());
        Assert.Equal(1500, root.GetProperty("server").GetProperty("monthlyCostTl").GetDouble());
        Assert.True(root.GetProperty("server").GetProperty("costPerActiveUserTl").GetDouble() > 0);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("learning").GetProperty("overrideRateTrend").ValueKind);
        Assert.True(root.GetProperty("energy").TryGetProperty("savedKwhWeek", out _));

        using var projection = await GetAsync(client, "/api/dashboard/projection?homes=100000");
        var p = projection.RootElement;
        Assert.Equal(100_000, p.GetProperty("homes").GetInt32());
        Assert.True(p.GetProperty("llmCostUsdMonth").GetDouble() > 0);
        Assert.Equal(20 * 1500, p.GetProperty("serverCostTlMonth").GetDouble());
        Assert.NotEqual(0, p.GetProperty("assumptions").GetArrayLength());
    }

    [Fact]
    public async Task AiCommand_FromTheApi_FeedsTheComfortCheckAndEnergyObservers()
    {
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/devices/ac-cocuk/commands", UriKind.Relative),
            new { type = "setpoint", value = 24, source = "ai", memberId = "ayse" },
            Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var scheduler = factory.Services.GetRequiredService<Application.Comfort.ComfortCheckScheduler>();

        Assert.True(scheduler.PendingCount >= 1);
    }
}

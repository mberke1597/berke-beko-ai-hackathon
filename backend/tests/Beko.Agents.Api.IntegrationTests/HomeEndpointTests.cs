using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Beko.Agents.Api.IntegrationTests;

public sealed class HomeEndpointTests(HomeApiFactory factory) : IClassFixture<HomeApiFactory>
{
    [Fact]
    public async Task HomesMe_ReturnsSeededHomeThroughHomeWhizClient()
    {
        using var client = factory.CreateClient();

        using var json = await GetJsonAsync(client, "/api/homes/me");
        var root = json.RootElement;

        Assert.Equal("home-1", root.GetProperty("home").GetProperty("id").GetString());
        Assert.Equal("Evim", root.GetProperty("home").GetProperty("name").GetString());
        Assert.Equal(
            ["salon", "yatak-odasi", "cocuk-odasi", "mutfak", "camasir-odasi"],
            root.GetProperty("rooms").EnumerateArray().Select(r => r.GetProperty("id").GetString()));

        var members = root.GetProperty("members").EnumerateArray().ToList();
        Assert.Equal(["ayse", "mehmet", "zeynep"], members.Select(m => m.GetProperty("id").GetString()));
        Assert.True(members[0].GetProperty("isDefault").GetBoolean());
        Assert.Equal("owner", members[0].GetProperty("role").GetString());

        var appliances = root.GetProperty("appliances").EnumerateArray().ToList();
        Assert.Equal(7, appliances.Count);
        var salon = appliances.Single(a => a.GetProperty("id").GetString() == "ac-salon");
        Assert.Equal("air_conditioner", salon.GetProperty("type").GetString());
        Assert.Equal(16, salon.GetProperty("capabilities").GetProperty("setpoint").GetProperty("min").GetDouble());
        var state = salon.GetProperty("state");
        Assert.Equal(JsonValueKind.Object, state.GetProperty("ac").ValueKind);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("program").ValueKind);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("oven").ValueKind);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("fridge").ValueKind);
    }

    [Fact]
    public async Task Command_RoundTrip_UpdatesDeviceAndRecordsEvent()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/devices/ac-cocuk/commands", UriKind.Relative),
            new { type = "setpoint", value = 21.5, source = "remote", memberId = "zeynep" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var accepted = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var commandId = accepted.RootElement.GetProperty("commandId").GetString();
        Assert.False(string.IsNullOrEmpty(commandId));
        Assert.True(accepted.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal(21.5, accepted.RootElement.GetProperty("appliance").GetProperty("state").GetProperty("ac").GetProperty("setpoint").GetDouble());

        using var device = await GetJsonAsync(client, "/api/devices/ac-cocuk");
        Assert.Equal(21.5, device.RootElement.GetProperty("state").GetProperty("ac").GetProperty("setpoint").GetDouble());

        var recorded = Assert.Single(factory.Events.Events, e => e.CommandId == commandId);
        Assert.Equal("home-1", recorded.HomeId);
        Assert.Equal("remote", recorded.Source);
        Assert.Equal("zeynep", recorded.MemberId);
        Assert.Equal(23, recorded.Before.Ac!.Setpoint);
        Assert.Equal(21.5, recorded.After.Ac!.Setpoint);
        Assert.Matches("^[0-9a-f]{32}$", recorded.TraceId);
        Assert.Matches("^[0-9a-f]{16}$", recorded.SpanId);
    }

    [Theory]
    [InlineData("ac-salon", "{\"type\":\"setpoint\",\"value\":40,\"source\":\"app\"}", "value_out_of_range")]
    [InlineData("fridge-1", "{\"type\":\"power\",\"value\":false,\"source\":\"app\"}", "command_not_supported")]
    public async Task Command_Rejected_Returns422ProblemDetails(string deviceId, string body, string code)
    {
        using var client = factory.CreateClient();

        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri($"/api/devices/{deviceId}/commands", UriKind.Relative), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(422, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(problem.RootElement.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrEmpty(problem.RootElement.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Command_InvalidSource_Returns400()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/devices/ac-salon/commands", UriKind.Relative),
            new { type = "power", value = true, source = "hacker" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnknownDevice_Returns404()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/devices/nope", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Telemetry_ReturnsLastDayOfSamples()
    {
        using var client = factory.CreateClient();

        using var json = await GetJsonAsync(client, "/api/devices/ac-salon/telemetry");

        var points = json.RootElement.EnumerateArray().ToList();
        Assert.True(points.Count > 200);
        Assert.True(points[0].TryGetProperty("roomTempC", out _));
        Assert.True(points[0].TryGetProperty("powerW", out _));
    }

    [Fact]
    public async Task MockHomeWhiz_RoomsListApplianceIds()
    {
        using var client = factory.CreateClient();

        using var json = await GetJsonAsync(client, "/api/mock/homewhiz/homes/home-1/rooms");

        var kitchen = json.RootElement.EnumerateArray().Single(r => r.GetProperty("id").GetString() == "mutfak");
        Assert.Equal(["dishwasher-1", "oven-1", "fridge-1"], kitchen.GetProperty("applianceIds").EnumerateArray().Select(a => a.GetString()));
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}

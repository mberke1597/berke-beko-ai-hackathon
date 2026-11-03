using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Beko.Agents.Application.Agents.Llm;
using Beko.Agents.TestSupport;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Beko.Agents.Api.IntegrationTests;

public sealed class ChatEndpointTests(AgentApiFactory factory) : IClassFixture<AgentApiFactory>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Agents_ListsCeliknazWithToolsAndWorkflowVersion()
    {
        using var client = factory.CreateClient();

        using var list = await GetJsonAsync(client, "/api/agents");
        var agent = Assert.Single(list.RootElement.EnumerateArray());
        Assert.Equal("celiknaz", agent.GetProperty("id").GetString());
        Assert.Equal("claude-sonnet-5-5", agent.GetProperty("model").GetString());

        using var details = await GetJsonAsync(client, "/api/agents/celiknaz");
        var root = details.RootElement;
        Assert.Equal(1, root.GetProperty("workflowVersion").GetInt32());
        Assert.Equal(
            ["get_home_status", "control_device"],
            root.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
        Assert.Equal(0, root.GetProperty("subagents").GetArrayLength());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("instructions").GetString()));
    }

    [Fact]
    public async Task PostMessage_RunsInBackground_AndStreamsRunCompleted()
    {
        factory.Script = (request, _) => request.Model == TestSettings.FastModel
            ? FakeLlmClient.Verdict("ok")
            : request.Messages[^1].Content.Any(c => c is LlmToolResult)
                ? FakeLlmClient.Text("Salonu 22 dereceye aldım.")
                : FakeLlmClient.ToolUse("t1", "control_device", """{"applianceId":"ac-cocuk","type":"setpoint","value":22}""");
        using var client = factory.CreateClient();
        var conversationId = await CreateConversationAsync(client);
        await using var hub = await ConnectAsync();
        var events = new List<string>();
        var completed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var name in new[] { "RunStarted", "StepStarted", "StepCompleted", "StepFailed", "TextDelta" })
        {
            hub.On<JsonElement>(name, _ =>
            {
                lock (events)
                {
                    events.Add(name);
                }
            });
        }

        hub.On<JsonElement>("RunCompleted", payload => completed.TrySetResult(payload.Clone()));
        await hub.InvokeAsync("JoinConversation", conversationId, TestContext.Current.CancellationToken);

        using var post = await client.PostAsJsonAsync(
            new Uri($"/api/conversations/{conversationId}/messages", UriKind.Relative),
            new { text = "Çocuk odasını 22 yap", channel = "voice" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        using var accepted = JsonDocument.Parse(await post.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var runId = accepted.RootElement.GetProperty("runId").GetString()!;
        var traceId = accepted.RootElement.GetProperty("traceId").GetString()!;
        Assert.Matches("^[0-9a-f]{32}$", traceId);

        var payload = await completed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(runId, payload.GetProperty("runId").GetString());
        Assert.Equal("Salonu 22 dereceye aldım.", payload.GetProperty("text").GetString());
        var action = Assert.Single(payload.GetProperty("actions").EnumerateArray());
        Assert.Equal("ac-cocuk", action.GetProperty("applianceId").GetString());
        Assert.Equal("Çocuk Odası Kliması", action.GetProperty("displayName").GetString());
        Assert.Equal("setpoint", action.GetProperty("command").GetString());
        Assert.Equal(22, action.GetProperty("value").GetDouble());
        Assert.True(action.GetProperty("accepted").GetBoolean());
        Assert.True(payload.GetProperty("totals").GetProperty("costUsd").GetDecimal() > 0);

        lock (events)
        {
            Assert.Equal("RunStarted", events[0]);
            Assert.Contains("StepStarted", events);
            Assert.Contains("StepCompleted", events);
            Assert.DoesNotContain("StepFailed", events);
        }

        // The AI command went through the app's device path with source "ai".
        using var device = await GetJsonAsync(client, "/api/devices/ac-cocuk");
        Assert.Equal(22, device.RootElement.GetProperty("state").GetProperty("ac").GetProperty("setpoint").GetDouble());
        Assert.Contains(factory.Events.Events, e => e.DeviceId == "ac-cocuk" && e.Source == "ai" && e.MemberId == "ayse");

        // The run is stored and can be read back.
        using var run = await GetRunWhenCompleteAsync(client, runId);
        Assert.Equal("completed", run.RootElement.GetProperty("status").GetString());
        Assert.Equal(traceId, run.RootElement.GetProperty("traceId").GetString());
        Assert.Equal(1, run.RootElement.GetProperty("workflowVersion").GetInt32());
        var steps = run.RootElement.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(
            ["trigger.user_message", "guard.input", "llm.call", "agent.llm", "llm.call", "tool.call", "llm.call", "guard.output", "respond"],
            steps.Select(s => s.GetProperty("type").GetString()));
        Assert.All(steps, s => Assert.Equal("completed", s.GetProperty("status").GetString()));

        // Messages: the user's and the assistant's answer with its action.
        using var messages = await GetJsonAsync(client, $"/api/conversations/{conversationId}/messages");
        var list = messages.RootElement.EnumerateArray().ToList();
        Assert.Equal(["user", "assistant"], list.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("Çocuk odasını 22 yap", list[0].GetProperty("text").GetString());
        Assert.Equal("Salonu 22 dereceye aldım.", list[1].GetProperty("text").GetString());
        Assert.Equal(runId, list[1].GetProperty("runId").GetString());
        Assert.Equal(1, list[1].GetProperty("actions").GetArrayLength());

        // The dashboard list.
        using var runs = await GetJsonAsync(client, "/api/runs?limit=50");
        var summary = runs.RootElement.EnumerateArray().Single(r => r.GetProperty("runId").GetString() == runId);
        Assert.Equal("Çocuk odasını 22 yap", summary.GetProperty("userText").GetString());
        Assert.Equal("celiknaz", summary.GetProperty("agentId").GetString());
        Assert.Equal(traceId, summary.GetProperty("traceId").GetString());
        Assert.True(summary.GetProperty("costUsd").GetDecimal() > 0);
    }

    [Fact]
    public async Task PostMessage_WhenModelFails_RunCompletedCarriesTurkishFallback()
    {
        factory.Script = (request, _) => request.Model == TestSettings.FastModel
            ? FakeLlmClient.Verdict("ok")
            : throw new InvalidOperationException("model down");
        using var client = factory.CreateClient();
        var conversationId = await CreateConversationAsync(client);
        await using var hub = await ConnectAsync();
        var completed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedSteps = 0;
        hub.On<JsonElement>("StepFailed", _ => Interlocked.Increment(ref failedSteps));
        hub.On<JsonElement>("RunCompleted", payload => completed.TrySetResult(payload.Clone()));
        await hub.InvokeAsync("JoinConversation", conversationId, TestContext.Current.CancellationToken);

        using var post = await client.PostAsJsonAsync(
            new Uri($"/api/conversations/{conversationId}/messages", UriKind.Relative),
            new { text = "Ev soğudu", channel = "text" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        using var accepted = JsonDocument.Parse(await post.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var runId = accepted.RootElement.GetProperty("runId").GetString()!;

        var payload = await completed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal("Şu anda yanıt veremiyorum, lütfen tekrar deneyin.", payload.GetProperty("text").GetString());
        Assert.True(failedSteps >= 1);
        using var run = await GetRunWhenCompleteAsync(client, runId, "failed");
        Assert.Equal("failed", run.RootElement.GetProperty("status").GetString());
        using var messages = await GetJsonAsync(client, $"/api/conversations/{conversationId}/messages");
        Assert.Equal(
            "Şu anda yanıt veremiyorum, lütfen tekrar deneyin.",
            messages.RootElement.EnumerateArray().Last().GetProperty("text").GetString());
    }

    [Fact]
    public async Task PostMessage_WithPersonalData_IsRefusedWithoutCallingTheModel()
    {
        var before = factory.Llm.Requests.Count;
        using var client = factory.CreateClient();
        var conversationId = await CreateConversationAsync(client);
        await using var hub = await ConnectAsync();
        var completed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<JsonElement>("RunCompleted", payload => completed.TrySetResult(payload.Clone()));
        await hub.InvokeAsync("JoinConversation", conversationId, TestContext.Current.CancellationToken);

        using var post = await client.PostAsJsonAsync(
            new Uri($"/api/conversations/{conversationId}/messages", UriKind.Relative),
            new { text = "Telefonum 0532 123 45 67", channel = "text" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

        var payload = await completed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Contains("kişisel bilgileri", payload.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal(before, factory.Llm.Requests.Count);
        using var messages = await GetJsonAsync(client, $"/api/conversations/{conversationId}/messages");
        Assert.Equal("Telefonum [TELEFON]", messages.RootElement.EnumerateArray().First().GetProperty("text").GetString());
    }

    [Fact]
    public async Task UnknownConversationOrAgent_Returns404()
    {
        using var client = factory.CreateClient();

        using var messages = await client.GetAsync(new Uri("/api/conversations/nope/messages", UriKind.Relative), TestContext.Current.CancellationToken);
        using var post = await client.PostAsJsonAsync(
            new Uri("/api/conversations/nope/messages", UriKind.Relative), new { text = "x" }, TestContext.Current.CancellationToken);
        using var create = await client.PostAsJsonAsync(
            new Uri("/api/conversations", UriKind.Relative), new { agentId = "nope" }, TestContext.Current.CancellationToken);
        using var agent = await client.GetAsync(new Uri("/api/agents/nope", UriKind.Relative), TestContext.Current.CancellationToken);
        using var run = await client.GetAsync(new Uri("/api/runs/nope", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, messages.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, agent.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, run.StatusCode);
    }

    [Theory]
    [InlineData("""{"text":"","channel":"text"}""")]
    [InlineData("""{"text":"merhaba","channel":"fax"}""")]
    public async Task PostMessage_InvalidBody_Returns400(string body)
    {
        using var client = factory.CreateClient();
        var conversationId = await CreateConversationAsync(client);

        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri($"/api/conversations/{conversationId}/messages", UriKind.Relative), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<string> CreateConversationAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/conversations", UriKind.Relative),
            new { agentId = "celiknaz", memberId = "ayse" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("celiknaz", json.RootElement.GetProperty("agentId").GetString());
        return json.RootElement.GetProperty("conversationId").GetString()!;
    }

    private async Task<HubConnection> ConnectAsync()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/runs"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();
        await connection.StartAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>The final run snapshot is written right after RunCompleted is sent, so poll briefly.</summary>
    private static async Task<JsonDocument> GetRunWhenCompleteAsync(HttpClient client, string runId, string status = "completed")
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            var json = await GetJsonAsync(client, $"/api/runs/{runId}");
            if (json.RootElement.GetProperty("status").GetString() == status || DateTime.UtcNow > deadline)
            {
                return json;
            }

            json.Dispose();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}

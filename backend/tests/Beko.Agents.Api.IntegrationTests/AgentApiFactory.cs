using Beko.Agents.Application.Agents.Llm;
using Beko.Agents.Application.Agents.Runs;
using Beko.Agents.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Beko.Agents.Api.IntegrationTests;

/// <summary>
/// Test host for the agent runtime: the model is a scripted fake (no network), Mongo stores and the agent
/// repository are in memory, HomeWhiz is the in-process mock. Everything else is the real app (controllers, SignalR,
/// workflow engine, step wrapper, run queue and worker).
/// </summary>
public sealed class AgentApiFactory : HomeApiFactory
{
    /// <summary>The model script of the current test. Haiku calls (guard.input) are answered with an ok verdict by default.</summary>
    public Func<LlmRequest, int, LlmResponse> Script { get; set; } = FakeLlmClient.Answering("Tamam.");

    public FakeLlmClient Llm { get; }

    public InMemoryConversationStore Conversations { get; } = new();

    public InMemoryRunStore Runs { get; } = new();

    public AgentApiFactory()
    {
        Llm = new FakeLlmClient((request, count) => Script(request, count));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        var (agent, workflow) = TestAgents.Celiknaz();
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ILlmClient>();
            services.AddSingleton<ILlmClient>(Llm);
            services.RemoveAll<IConversationStore>();
            services.AddSingleton<IConversationStore>(Conversations);
            services.RemoveAll<IRunStore>();
            services.AddSingleton<IRunStore>(Runs);
            services.RemoveAll<IAgentRepository>();
            services.AddSingleton<IAgentRepository>(new FakeAgentRepository(agent, workflow));
            services.RemoveAll<ICostRollup>();
            services.AddSingleton<ICostRollup>(new FakeCostRollup());
        });
    }
}

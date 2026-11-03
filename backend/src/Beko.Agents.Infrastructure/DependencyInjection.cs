using Anthropic;
using Beko.Agents.Application.Agents;
using Beko.Agents.Application.Agents.Guardrails;
using Beko.Agents.Application.Agents.Llm;
using Beko.Agents.Application.Agents.Runs;
using Beko.Agents.Application.Agents.Tools;
using Beko.Agents.Application.Agents.Workflow;
using Beko.Agents.Application.Homes;
using Beko.Agents.Application.Options;
using Beko.Agents.Infrastructure.Agents;
using Beko.Agents.Infrastructure.HomeWhiz;
using Beko.Agents.Infrastructure.HomeWhiz.Mock;
using Beko.Agents.Infrastructure.Llm;
using Beko.Agents.Infrastructure.Persistence.Mongo;
using Beko.Agents.Infrastructure.Persistence.Postgres;
using Beko.Agents.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Beko.Agents.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Tag for health checks that belong to <c>/health/ready</c>.</summary>
    public const string ReadyTag = "ready";

    public const string PostgresConnectionStringName = "Postgres";

    private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Registers options, persistence, health checks and telemetry.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        AddOptions(services, configuration, requireSecrets: environment.IsProduction());

        var postgresConnectionString = configuration.GetConnectionString(PostgresConnectionStringName);
        if (string.IsNullOrWhiteSpace(postgresConnectionString))
        {
            throw new InvalidOperationException($"ConnectionStrings:{PostgresConnectionStringName} is required.");
        }

        services.AddDbContext<AgentsDbContext>(options => options
            .UseNpgsql(postgresConnectionString)
            .UseSnakeCaseNamingConvention());

        services.AddSingleton<IMongoClient>(sp =>
            new MongoClient(sp.GetRequiredService<IOptions<MongoOptions>>().Value.ConnectionString));
        services.AddSingleton(sp =>
            sp.GetRequiredService<IMongoClient>().GetDatabase(sp.GetRequiredService<IOptions<MongoOptions>>().Value.Database));

        services.AddSingleton<IDeviceEventStore, MongoDeviceEventStore>();

        AddHomeWhiz(services);
        AddAgentRuntime(services);

        services.AddHealthChecks()
            .AddDbContextCheck<AgentsDbContext>("postgres", tags: [ReadyTag])
            .AddCheck<MongoHealthCheck>("mongodb", tags: [ReadyTag], timeout: HealthCheckTimeout);

        var appVersion = configuration.GetSection(AppOptions.SectionName)[nameof(AppOptions.Version)] ?? "0.0.0";
        services.AddBekoTelemetry(configuration, appVersion);

        return services;
    }

    private static void AddOptions(IServiceCollection services, IConfiguration configuration, bool requireSecrets)
    {
        AddValidatedOptions<AppOptions>(services, configuration, AppOptions.SectionName);
        AddValidatedOptions<AnthropicOptions>(services, configuration, AnthropicOptions.SectionName);
        AddValidatedOptions<MongoOptions>(services, configuration, MongoOptions.SectionName);
        AddValidatedOptions<JwtOptions>(services, configuration, JwtOptions.SectionName);
        AddValidatedOptions<PricingOptions>(services, configuration, PricingOptions.SectionName);
        AddValidatedOptions<HomeWhizOptions>(services, configuration, HomeWhizOptions.SectionName);
        AddValidatedOptions<SimulationOptions>(services, configuration, SimulationOptions.SectionName);

        services.AddSingleton<IValidateOptions<AnthropicOptions>>(new AnthropicOptionsValidator(requireSecrets));
        services.AddSingleton<IValidateOptions<JwtOptions>>(new JwtOptionsValidator(requireSecrets));
        services.AddSingleton<IValidateOptions<PricingOptions>, PricingOptionsValidator>();
    }

    /// <summary>HomeWhiz client plus the mock HomeWhiz (in-memory store and device simulator) it points to today.</summary>
    private static void AddHomeWhiz(IServiceCollection services)
    {
        services.AddHttpClient<IHomeWhizClient, HomeWhizHttpClient>((sp, client) =>
        {
            var baseUrl = sp.GetRequiredService<IOptions<HomeWhizOptions>>().Value.BaseUrl;
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        });

        services.AddSingleton<SimulationClock>();
        services.AddSingleton<MockHomeWhizStore>();
        services.AddHostedService<DeviceSimulator>();
    }

    /// <summary>Applies pending EF Core migrations (used on startup in Development only).</summary>
    public static async Task MigrateDatabaseAsync(this IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        await db.Database.MigrateAsync();
    }

    /// <summary>Agent runtime: LLM client, step wrapper, workflow engine and executors, tools, stores and the run queue.</summary>
    private static void AddAgentRuntime(IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AnthropicOptions>>().Value;
            return new AnthropicClient
            {
                ApiKey = options.ApiKey,
                BaseUrl = options.BaseUrl,
                Timeout = AnthropicLlmClient.DefaultTimeout,
                MaxRetries = AnthropicLlmClient.DefaultMaxRetries,
            };
        });
        services.AddSingleton<ILlmClient, AnthropicLlmClient>();
        services.AddSingleton<ICostCalculator, CostCalculator>();

        services.AddSingleton<IConversationStore, MongoConversationStore>();
        services.AddSingleton<IRunStore, MongoRunStore>();
        services.AddScoped<IAgentRepository, PostgresAgentRepository>();
        services.AddScoped<ICostRollup, PostgresCostRollup>();

        services.AddScoped<IAgentTool, GetHomeStatusTool>();
        services.AddScoped<IAgentTool, ControlDeviceTool>();
        services.AddScoped<IToolRegistry, ToolRegistry>();

        services.AddScoped<IStepExecutor, UserMessageTriggerExecutor>();
        services.AddScoped<IStepExecutor, InputGuardExecutor>();
        services.AddScoped<IStepExecutor, OutputGuardExecutor>();
        services.AddScoped<IStepExecutor, AgentLlmExecutor>();
        services.AddScoped<IStepExecutor, RespondExecutor>();
        services.AddScoped<RunRecorder>();
        services.AddScoped<WorkflowEngine>();
        services.AddScoped<ChatService>();
        services.AddScoped<DeviceService>();

        services.AddSingleton<ChannelRunDispatcher>();
        services.AddSingleton<IRunDispatcher>(sp => sp.GetRequiredService<ChannelRunDispatcher>());
        services.AddHostedService<RunWorker>();
    }

    private static void AddValidatedOptions<TOptions>(IServiceCollection services, IConfiguration configuration, string sectionName)
        where TOptions : class
    {
        services.AddOptions<TOptions>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }
}

using Beko.Agents.Application.Options;
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

        services.AddDbContext<AgentsDbContext>(options => options.UseNpgsql(postgresConnectionString));

        services.AddSingleton<IMongoClient>(sp =>
            new MongoClient(sp.GetRequiredService<IOptions<MongoOptions>>().Value.ConnectionString));
        services.AddSingleton(sp =>
            sp.GetRequiredService<IMongoClient>().GetDatabase(sp.GetRequiredService<IOptions<MongoOptions>>().Value.Database));

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

        services.AddSingleton<IValidateOptions<AnthropicOptions>>(new AnthropicOptionsValidator(requireSecrets));
        services.AddSingleton<IValidateOptions<JwtOptions>>(new JwtOptionsValidator(requireSecrets));
        services.AddSingleton<IValidateOptions<PricingOptions>, PricingOptionsValidator>();
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

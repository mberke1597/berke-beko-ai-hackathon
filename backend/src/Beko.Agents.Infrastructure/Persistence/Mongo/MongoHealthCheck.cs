using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Beko.Agents.Infrastructure.Persistence.Mongo;

/// <summary>Readiness check that sends a <c>ping</c> command to the configured MongoDB database.</summary>
internal sealed class MongoHealthCheck(IMongoDatabase database) : IHealthCheck
{
    private static readonly BsonDocument PingCommand = new("ping", 1);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await database.RunCommandAsync<BsonDocument>(PingCommand, cancellationToken: cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "MongoDB ping failed.", ex);
        }
    }
}

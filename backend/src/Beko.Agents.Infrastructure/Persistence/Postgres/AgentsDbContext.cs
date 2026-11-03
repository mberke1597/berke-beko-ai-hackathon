using Microsoft.EntityFrameworkCore;

namespace Beko.Agents.Infrastructure.Persistence.Postgres;

/// <summary>EF Core context for PostgreSQL (configuration, RBAC, workflows, knowledge, cost aggregates).</summary>
public sealed class AgentsDbContext(DbContextOptions<AgentsDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AgentsDbContext).Assembly);
    }
}

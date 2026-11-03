using Beko.Agents.Domain.Agents;
using Microsoft.EntityFrameworkCore;

namespace Beko.Agents.Infrastructure.Persistence.Postgres;

/// <summary>EF Core context for PostgreSQL (configuration, RBAC, workflows, knowledge, cost aggregates).</summary>
public sealed class AgentsDbContext(DbContextOptions<AgentsDbContext> options) : DbContext(options)
{
    public DbSet<Agent> Agents => Set<Agent>();

    public DbSet<ToolDefinition> Tools => Set<ToolDefinition>();

    public DbSet<TonePreset> TonePresets => Set<TonePreset>();

    public DbSet<WorkflowDefinition> WorkflowDefinitions => Set<WorkflowDefinition>();

    public DbSet<WorkflowStep> WorkflowSteps => Set<WorkflowStep>();

    public DbSet<CostDaily> CostDaily => Set<CostDaily>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AgentsDbContext).Assembly);
    }
}

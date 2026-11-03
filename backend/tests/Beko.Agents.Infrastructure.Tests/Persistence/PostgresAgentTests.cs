using Beko.Agents.Application.Agents.Llm;
using Beko.Agents.Domain.Agents;
using Beko.Agents.Infrastructure.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Beko.Agents.Infrastructure.Tests.Persistence;

/// <summary>Real PostgreSQL (Docker): the migration applies, the seed is right and the cost rollup is atomic.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17.11-alpine").Build();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    public AgentsDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AgentsDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .Options);
}

public sealed class PostgresAgentTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Migration_LeavesNoPendingModelChanges()
    {
        await using var db = postgres.CreateContext();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Seed_HasCeliknazWithItsTwoToolsAndTone()
    {
        await using var db = postgres.CreateContext();
        var repository = new PostgresAgentRepository(db);

        var agent = await repository.GetAgentAsync("celiknaz", TestContext.Current.CancellationToken);

        Assert.NotNull(agent);
        Assert.Equal("Çeliknaz", agent.Name);
        Assert.Null(agent.Model);
        Assert.Contains("kısa", agent.Instructions, StringComparison.Ordinal);
        Assert.Equal(["control_device", "get_home_status"], agent.Tools.Select(t => t.ToolName).Order());
        Assert.All(agent.Tools, t => Assert.False(string.IsNullOrEmpty(t.Tool?.Description)));
        Assert.Empty(agent.Subagents);
        var tone = await repository.GetTonePresetAsync(agent.TonePresetId!, TestContext.Current.CancellationToken);
        Assert.Equal("Samimi", tone?.Name);
        Assert.Single(await repository.GetAgentsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Seed_WorkflowV1HasTheGuardedStepsInOrder()
    {
        await using var db = postgres.CreateContext();
        var repository = new PostgresAgentRepository(db);

        var workflow = await repository.GetActiveWorkflowAsync("celiknaz", TestContext.Current.CancellationToken);

        Assert.NotNull(workflow);
        Assert.Equal(1, workflow.Version);
        Assert.Equal(
            [StepTypes.TriggerUserMessage, StepTypes.GuardInput, StepTypes.AgentLlm, StepTypes.GuardOutput, StepTypes.Respond],
            workflow.Steps.OrderBy(s => s.Position).Select(s => s.Type));
        Assert.All(workflow.Steps, s => Assert.Null(s.ParentStepId));
        Assert.Contains("maxIterations", workflow.Steps.Single(s => s.Type == StepTypes.AgentLlm).Config, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyOneWorkflowVersionPerAgentCanBeActive()
    {
        await using var db = postgres.CreateContext();
        db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = Guid.NewGuid(), AgentId = "celiknaz", Version = 2, IsActive = true, CreatedAtUtc = DateTime.UtcNow });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CostRollup_AddsCallsTokensAndCostPerDayAgentAndModel()
    {
        var day = new DateOnly(2026, 11, 3);
        await using (var db = postgres.CreateContext())
        {
            var rollup = new PostgresCostRollup(db);
            await rollup.AddAsync(day, "celiknaz", "claude-sonnet-5-5", new LlmUsage(1000, 200, 50, 10), 0.004m, TestContext.Current.CancellationToken);
            await rollup.AddAsync(day, "celiknaz", "claude-sonnet-5-5", new LlmUsage(500, 100, 0, 0), 0.002m, TestContext.Current.CancellationToken);
            await rollup.AddAsync(day, "celiknaz", "claude-haiku-5-5", new LlmUsage(300, 30, 0, 0), 0.0001m, TestContext.Current.CancellationToken);
        }

        await using var read = postgres.CreateContext();
        var rows = await read.CostDaily.Where(c => c.Day == day).ToListAsync(TestContext.Current.CancellationToken);
        var sonnet = Assert.Single(rows, r => r.Model == "claude-sonnet-5-5");
        Assert.Equal(2, sonnet.Calls);
        Assert.Equal(1500, sonnet.InputTokens);
        Assert.Equal(300, sonnet.OutputTokens);
        Assert.Equal(50, sonnet.CacheReadTokens);
        Assert.Equal(10, sonnet.CacheWriteTokens);
        Assert.Equal(0.006m, sonnet.CostUsd);
        Assert.Equal(1, Assert.Single(rows, r => r.Model == "claude-haiku-5-5").Calls);
    }
}

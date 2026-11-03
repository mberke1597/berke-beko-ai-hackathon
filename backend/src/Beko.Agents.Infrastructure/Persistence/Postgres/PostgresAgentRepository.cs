using Beko.Agents.Application.Agents.Llm;
using Beko.Agents.Application.Agents.Runs;
using Beko.Agents.Domain.Agents;
using Microsoft.EntityFrameworkCore;

namespace Beko.Agents.Infrastructure.Persistence.Postgres;

internal sealed class PostgresAgentRepository(AgentsDbContext db) : IAgentRepository
{
    public async Task<IReadOnlyList<Agent>> GetAgentsAsync(CancellationToken cancellationToken) =>
        await db.Agents.AsNoTracking().OrderBy(a => a.Name).ToListAsync(cancellationToken);

    public Task<Agent?> GetAgentAsync(string agentId, CancellationToken cancellationToken) =>
        db.Agents.AsNoTracking()
            .Include(a => a.Tools.Where(t => t.Tool!.Enabled)).ThenInclude(t => t.Tool)
            .Include(a => a.Subagents)
            .AsSplitQuery()
            .FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);

    public Task<WorkflowDefinition?> GetActiveWorkflowAsync(string agentId, CancellationToken cancellationToken) =>
        db.WorkflowDefinitions.AsNoTracking()
            .Include(w => w.Steps)
            .FirstOrDefaultAsync(w => w.AgentId == agentId && w.IsActive, cancellationToken);

    public Task<TonePreset?> GetTonePresetAsync(string tonePresetId, CancellationToken cancellationToken) =>
        db.TonePresets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tonePresetId, cancellationToken);
}

/// <summary>Adds one LLM call to the day's row in <c>cost_daily</c> with an atomic upsert.</summary>
internal sealed class PostgresCostRollup(AgentsDbContext db) : ICostRollup
{
    public Task AddAsync(DateOnly day, string agentId, string model, LlmUsage usage, decimal costUsd, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO cost_daily (day, agent_id, model, calls, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost_usd)
            VALUES ({day}, {agentId}, {model}, 1, {usage.InputTokens}, {usage.OutputTokens}, {usage.CacheReadTokens}, {usage.CacheWriteTokens}, {costUsd})
            ON CONFLICT (day, agent_id, model) DO UPDATE SET
                calls = cost_daily.calls + 1,
                input_tokens = cost_daily.input_tokens + EXCLUDED.input_tokens,
                output_tokens = cost_daily.output_tokens + EXCLUDED.output_tokens,
                cache_read_tokens = cost_daily.cache_read_tokens + EXCLUDED.cache_read_tokens,
                cache_write_tokens = cost_daily.cache_write_tokens + EXCLUDED.cache_write_tokens,
                cost_usd = cost_daily.cost_usd + EXCLUDED.cost_usd
            """,
            cancellationToken);
    }
}

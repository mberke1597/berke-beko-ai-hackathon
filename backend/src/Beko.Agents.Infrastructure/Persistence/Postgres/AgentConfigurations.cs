using Beko.Agents.Domain.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beko.Agents.Infrastructure.Persistence.Postgres;

internal sealed class AgentConfiguration : IEntityTypeConfiguration<Agent>
{
    public void Configure(EntityTypeBuilder<Agent> builder)
    {
        builder.ToTable("agents");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasMaxLength(64);
        builder.Property(a => a.Name).HasMaxLength(200);
        builder.Property(a => a.Model).HasMaxLength(100);
        builder.Property(a => a.TonePresetId).HasMaxLength(64);
        builder.HasOne<TonePreset>().WithMany().HasForeignKey(a => a.TonePresetId).OnDelete(DeleteBehavior.SetNull);
        builder.HasMany(a => a.Tools).WithOne().HasForeignKey(t => t.AgentId);
        builder.HasMany(a => a.Subagents).WithOne().HasForeignKey(s => s.ParentAgentId);
        builder.HasData(AgentSeed.Celiknaz);
    }
}

internal sealed class ToolDefinitionConfiguration : IEntityTypeConfiguration<ToolDefinition>
{
    public void Configure(EntityTypeBuilder<ToolDefinition> builder)
    {
        builder.ToTable("tools");
        builder.HasKey(t => t.Name);
        builder.Property(t => t.Name).HasMaxLength(100);
        builder.Property(t => t.InputSchema).HasColumnType("jsonb");
        builder.HasData(AgentSeed.Tools);
    }
}

internal sealed class AgentToolConfiguration : IEntityTypeConfiguration<AgentTool>
{
    public void Configure(EntityTypeBuilder<AgentTool> builder)
    {
        builder.ToTable("agent_tools");
        builder.HasKey(t => new { t.AgentId, t.ToolName });
        builder.Property(t => t.ToolName).HasMaxLength(100);
        builder.HasOne(t => t.Tool).WithMany().HasForeignKey(t => t.ToolName);
        builder.HasData(AgentSeed.AgentTools);
    }
}

internal sealed class AgentSubagentConfiguration : IEntityTypeConfiguration<AgentSubagent>
{
    public void Configure(EntityTypeBuilder<AgentSubagent> builder)
    {
        builder.ToTable("agent_subagents");
        builder.HasKey(s => new { s.ParentAgentId, s.SubagentId });
        builder.Property(s => s.SubagentId).HasMaxLength(64);
        builder.HasOne<Agent>().WithMany().HasForeignKey(s => s.SubagentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TonePresetConfiguration : IEntityTypeConfiguration<TonePreset>
{
    public void Configure(EntityTypeBuilder<TonePreset> builder)
    {
        builder.ToTable("tone_presets");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasMaxLength(64);
        builder.Property(t => t.Name).HasMaxLength(100);
        builder.HasData(AgentSeed.TonePresets);
    }
}

internal sealed class WorkflowDefinitionConfiguration : IEntityTypeConfiguration<WorkflowDefinition>
{
    public void Configure(EntityTypeBuilder<WorkflowDefinition> builder)
    {
        builder.ToTable("workflow_definitions");
        builder.HasKey(w => w.Id);
        builder.Property(w => w.AgentId).HasMaxLength(64);
        builder.Property(w => w.CreatedBy).HasMaxLength(100);
        builder.HasIndex(w => new { w.AgentId, w.Version }).IsUnique();
        builder.HasIndex(w => w.AgentId).HasFilter("is_active").IsUnique();
        builder.HasOne<Agent>().WithMany().HasForeignKey(w => w.AgentId);
        builder.HasMany(w => w.Steps).WithOne().HasForeignKey(s => s.DefinitionId);
        builder.HasData(AgentSeed.CeliknazWorkflowV1);
    }
}

internal sealed class WorkflowStepConfiguration : IEntityTypeConfiguration<WorkflowStep>
{
    public void Configure(EntityTypeBuilder<WorkflowStep> builder)
    {
        builder.ToTable("workflow_steps");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Type).HasMaxLength(64);
        builder.Property(s => s.Name).HasMaxLength(200);
        builder.Property(s => s.Config).HasColumnType("jsonb");
        builder.HasOne<WorkflowStep>().WithMany().HasForeignKey(s => s.ParentStepId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(s => new { s.DefinitionId, s.ParentStepId, s.Position });
        builder.HasData(AgentSeed.CeliknazWorkflowV1Steps);
    }
}

internal sealed class CostDailyConfiguration : IEntityTypeConfiguration<CostDaily>
{
    public void Configure(EntityTypeBuilder<CostDaily> builder)
    {
        builder.ToTable("cost_daily");
        builder.HasKey(c => new { c.Day, c.AgentId, c.Model });
        builder.Property(c => c.AgentId).HasMaxLength(64);
        builder.Property(c => c.Model).HasMaxLength(100);
        builder.Property(c => c.CostUsd).HasPrecision(18, 8);
    }
}

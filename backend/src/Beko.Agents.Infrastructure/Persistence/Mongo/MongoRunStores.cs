using System.Text;
using Beko.Agents.Application.Agents.Runs;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

namespace Beko.Agents.Infrastructure.Persistence.Mongo;

/// <summary>
/// BSON mapping for the run records: snake_case field names (<c>trace_id</c>, <c>run_id</c>, ...), decimals as
/// Decimal128 and unknown fields (such as <c>_id</c>) ignored on read.
/// </summary>
internal static class MongoRunMapping
{
    private static readonly Lock Gate = new();
    private static bool _registered;

    public static void Register()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            var pack = new ConventionPack
            {
                new SnakeCaseElementNameConvention(),
                new IgnoreExtraElementsConvention(true),
            };
            ConventionRegistry.Register(
                "beko-runs",
                pack,
                type => type.Namespace?.StartsWith("Beko.Agents.Application.Agents", StringComparison.Ordinal) == true);

            BsonSerializer.TryRegisterSerializer(new DecimalSerializer(BsonType.Decimal128));
            BsonSerializer.TryRegisterSerializer(new ObjectSerializer(type =>
                ObjectSerializer.DefaultAllowedTypes(type)
                || type.FullName?.StartsWith("System.Collections.Generic", StringComparison.Ordinal) == true));
            _registered = true;
        }
    }

    private sealed class SnakeCaseElementNameConvention : ConventionBase, IMemberMapConvention
    {
        public void Apply(BsonMemberMap memberMap) => memberMap.SetElementName(ToSnakeCase(memberMap.MemberName));

        private static string ToSnakeCase(string name)
        {
            var builder = new StringBuilder(name.Length + 8);
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (char.IsUpper(c))
                {
                    if (i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                    {
                        builder.Append('_');
                    }

                    builder.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }
    }
}

/// <summary>Mongo <c>conversations</c> and <c>messages</c>.</summary>
internal sealed class MongoConversationStore : IConversationStore
{
    private readonly IMongoCollection<ConversationRecord> _conversations;
    private readonly IMongoCollection<MessageRecord> _messages;

    public MongoConversationStore(IMongoDatabase database)
    {
        MongoRunMapping.Register();
        _conversations = database.GetCollection<ConversationRecord>("conversations");
        _messages = database.GetCollection<MessageRecord>("messages");
    }

    public Task AddConversationAsync(ConversationRecord conversation, CancellationToken cancellationToken) =>
        _conversations.InsertOneAsync(conversation, options: null, cancellationToken);

    public async Task<ConversationRecord?> GetConversationAsync(string conversationId, CancellationToken cancellationToken) =>
        await _conversations.Find(c => c.ConversationId == conversationId).FirstOrDefaultAsync(cancellationToken);

    public Task AddMessageAsync(MessageRecord message, CancellationToken cancellationToken) =>
        _messages.InsertOneAsync(message, options: null, cancellationToken);

    public async Task<IReadOnlyList<MessageRecord>> GetMessagesAsync(string conversationId, CancellationToken cancellationToken) =>
        await _messages.Find(m => m.ConversationId == conversationId).SortBy(m => m.CreatedAtUtc).ToListAsync(cancellationToken);
}

/// <summary>Mongo <c>runs</c>, <c>run_steps</c>, <c>llm_calls</c> and <c>tool_calls</c>.</summary>
internal sealed class MongoRunStore : IRunStore
{
    private static readonly ReplaceOptions Upsert = new() { IsUpsert = true };

    private readonly IMongoCollection<RunRecord> _runs;
    private readonly IMongoCollection<StepRecord> _steps;
    private readonly IMongoCollection<LlmCallRecord> _llmCalls;
    private readonly IMongoCollection<ToolCallRecord> _toolCalls;
    private readonly IMongoCollection<GuardrailDecisionRecord> _guardrailDecisions;

    public MongoRunStore(IMongoDatabase database)
    {
        MongoRunMapping.Register();
        _runs = database.GetCollection<RunRecord>("runs");
        _steps = database.GetCollection<StepRecord>("run_steps");
        _llmCalls = database.GetCollection<LlmCallRecord>("llm_calls");
        _toolCalls = database.GetCollection<ToolCallRecord>("tool_calls");
        _guardrailDecisions = database.GetCollection<GuardrailDecisionRecord>("guardrail_decisions");
    }

    public Task SaveRunAsync(RunRecord run, CancellationToken cancellationToken) =>
        _runs.ReplaceOneAsync(r => r.RunId == run.RunId, run, Upsert, cancellationToken);

    public Task SaveStepAsync(StepRecord step, CancellationToken cancellationToken) =>
        _steps.ReplaceOneAsync(s => s.StepId == step.StepId, step, Upsert, cancellationToken);

    public Task AddLlmCallAsync(LlmCallRecord call, CancellationToken cancellationToken) =>
        _llmCalls.InsertOneAsync(call, options: null, cancellationToken);

    public Task AddToolCallAsync(ToolCallRecord call, CancellationToken cancellationToken) =>
        _toolCalls.InsertOneAsync(call, options: null, cancellationToken);

    public Task AddGuardrailDecisionAsync(GuardrailDecisionRecord decision, CancellationToken cancellationToken) =>
        _guardrailDecisions.InsertOneAsync(decision, options: null, cancellationToken);

    public async Task<IReadOnlyList<RunRecord>> GetRecentRunsAsync(int limit, CancellationToken cancellationToken) =>
        await _runs.Find(FilterDefinition<RunRecord>.Empty).SortByDescending(r => r.StartedAtUtc).Limit(limit).ToListAsync(cancellationToken);

    public async Task<RunRecord?> GetRunAsync(string runId, CancellationToken cancellationToken) =>
        await _runs.Find(r => r.RunId == runId).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<StepRecord>> GetStepsAsync(string runId, CancellationToken cancellationToken) =>
        await _steps.Find(s => s.RunId == runId).SortBy(s => s.Sequence).ToListAsync(cancellationToken);
}

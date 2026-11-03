using Beko.Agents.Application.Dashboard;
using Beko.Agents.Application.Learning;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Beko.Agents.Infrastructure.Persistence.Mongo;

/// <summary>Reads the <c>device_events</c> documents written by <see cref="MongoDeviceEventStore"/>.</summary>
internal sealed class MongoDeviceEventReader(IMongoDatabase database) : IDeviceEventReader
{
    private readonly IMongoCollection<BsonDocument> _collection = database.GetCollection<BsonDocument>(MongoDeviceEventStore.CollectionName);

    public async Task<IReadOnlyList<DeviceEventView>> GetAsync(string homeId, DateTime sinceUtc, CancellationToken cancellationToken)
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("homeId", homeId),
            Builders<BsonDocument>.Filter.Gte("atUtc", sinceUtc));
        var documents = await _collection.Find(filter).Sort(Builders<BsonDocument>.Sort.Ascending("atUtc")).ToListAsync(cancellationToken);
        return [.. documents.Select(ToView)];
    }

    private static DeviceEventView ToView(BsonDocument d)
    {
        var command = d.GetValue("command", new BsonDocument()).AsBsonDocument;
        var value = command.GetValue("value", BsonNull.Value);
        var ac = d.GetValue("after", new BsonDocument()).AsBsonDocument.GetValue("ac", BsonNull.Value);
        return new DeviceEventView(
            d.GetValue("commandId", string.Empty).AsString,
            d.GetValue("deviceId", string.Empty).AsString,
            d.GetValue("source", string.Empty).AsString,
            d.GetValue("memberId", string.Empty).AsString,
            command.GetValue("type", string.Empty).AsString,
            value.IsNumeric ? value.ToDouble() : null,
            value.IsBoolean ? value.AsBoolean : null,
            ac.IsBsonDocument && ac.AsBsonDocument.GetValue("setpoint", BsonNull.Value) is { IsNumeric: true } sp ? sp.ToDouble() : null,
            ac.IsBsonDocument && ac.AsBsonDocument.GetValue("mode", BsonNull.Value) is { IsString: true } mode ? mode.AsString : null,
            d["atUtc"].ToUniversalTime());
    }
}

/// <summary>Mongo <c>comfort_feedback</c>.</summary>
internal sealed class MongoComfortFeedbackStore(IMongoDatabase database) : IComfortFeedbackStore
{
    private readonly IMongoCollection<BsonDocument> _collection = database.GetCollection<BsonDocument>("comfort_feedback");

    public Task AddAsync(ComfortFeedbackRecord feedback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        var document = new BsonDocument
        {
            { "id", feedback.Id },
            { "homeId", feedback.HomeId },
            { "memberId", feedback.MemberId },
            { "applianceId", feedback.ApplianceId },
            { "sensation", feedback.Sensation },
            { "clothing", feedback.Clothing is null ? BsonNull.Value : new BsonString(feedback.Clothing) },
            { "commandId", feedback.CommandId is null ? BsonNull.Value : new BsonString(feedback.CommandId) },
            { "roomTempC", feedback.RoomTempC },
            { "humidityPct", feedback.HumidityPct },
            { "pmvBase", feedback.PmvBase },
            { "clo", feedback.Clo },
            { "atUtc", feedback.AtUtc },
            { "trace_id", feedback.TraceId is null ? BsonNull.Value : new BsonString(feedback.TraceId) },
            { "span_id", feedback.SpanId is null ? BsonNull.Value : new BsonString(feedback.SpanId) },
        };
        return _collection.InsertOneAsync(document, options: null, cancellationToken);
    }

    public async Task<IReadOnlyList<ComfortFeedbackRecord>> GetAsync(string homeId, DateTime sinceUtc, CancellationToken cancellationToken)
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("homeId", homeId),
            Builders<BsonDocument>.Filter.Gte("atUtc", sinceUtc));
        var documents = await _collection.Find(filter).Sort(Builders<BsonDocument>.Sort.Ascending("atUtc")).ToListAsync(cancellationToken);
        return
        [
            .. documents.Select(d => new ComfortFeedbackRecord(
                d["id"].AsString,
                d["homeId"].AsString,
                d["memberId"].AsString,
                d["applianceId"].AsString,
                d["sensation"].ToInt32(),
                d["clothing"].IsString ? d["clothing"].AsString : null,
                d["commandId"].IsString ? d["commandId"].AsString : null,
                d["roomTempC"].ToDouble(),
                d["humidityPct"].ToDouble(),
                d["pmvBase"].ToDouble(),
                d["clo"].ToDouble(),
                d["atUtc"].ToUniversalTime(),
                d["trace_id"].IsString ? d["trace_id"].AsString : null,
                d["span_id"].IsString ? d["span_id"].AsString : null)),
        ];
    }
}

/// <summary>
/// LLM statistics from the runtime's <c>runs</c> and <c>tool_calls</c> collections (snake_case documents, see <c>MongoRunMapping</c>).
/// Missing collections or fields count as zero.
/// </summary>
internal sealed class MongoRunStatsReader(IMongoDatabase database) : IRunStatsReader
{
    public async Task<RunStats> GetAsync(DateTime todayStartUtc, DateTime monthStartUtc, CancellationToken cancellationToken)
    {
        var runs = database.GetCollection<BsonDocument>("runs");
        var monthRuns = await runs
            .Find(Builders<BsonDocument>.Filter.Gte("started_at_utc", monthStartUtc))
            .Project(Builders<BsonDocument>.Projection.Include("started_at_utc").Include("conversation_id").Include("duration_ms").Include("status").Include("totals"))
            .ToListAsync(cancellationToken);

        var rows = monthRuns.Select(r =>
        {
            var totals = r.GetValue("totals", new BsonDocument()).AsBsonDocument;
            return new
            {
                Started = r["started_at_utc"].ToUniversalTime(),
                Conversation = r.GetValue("conversation_id", string.Empty).ToString() ?? string.Empty,
                DurationMs = Number(r.GetValue("duration_ms", 0)),
                Cost = Number(totals.GetValue("cost_usd", 0)),
                Tokens = Number(totals.GetValue("input_tokens", 0)) + Number(totals.GetValue("output_tokens", 0)),
                Done = r.GetValue("status", string.Empty).ToString() != "running",
            };
        }).ToList();

        var today = rows.Where(r => r.Started >= todayStartUtc).ToList();
        var conversationsMonth = rows.Select(r => r.Conversation).Distinct().Count();
        var finished = rows.Where(r => r.Done).ToList();

        var toolDocs = await database.GetCollection<BsonDocument>("tool_calls")
            .Find(Builders<BsonDocument>.Filter.Gte("at_utc", monthStartUtc))
            .Project(Builders<BsonDocument>.Projection.Include("tool").Include("is_error"))
            .ToListAsync(cancellationToken);
        var tools = toolDocs
            .GroupBy(t => t.GetValue("tool", "unknown").ToString() ?? "unknown")
            .Select(g => new ToolStat(g.Key, g.LongCount(), g.LongCount(t => t.GetValue("is_error", false).ToBoolean())))
            .OrderByDescending(t => t.Calls)
            .ToList();

        var monthCost = rows.Sum(r => r.Cost);
        return new RunStats(
            today.Count,
            today.Select(r => r.Conversation).Distinct().Count(),
            Math.Round(today.Sum(r => r.Cost), 4),
            Math.Round(monthCost, 4),
            (long)today.Sum(r => r.Tokens),
            conversationsMonth == 0 ? 0 : Math.Round(monthCost / conversationsMonth, 4),
            finished.Count == 0 ? 0 : Math.Round(finished.Average(r => r.DurationMs), 0),
            tools);
    }

    private static double Number(BsonValue value) => value.IsNumeric ? value.ToDouble() : 0;
}

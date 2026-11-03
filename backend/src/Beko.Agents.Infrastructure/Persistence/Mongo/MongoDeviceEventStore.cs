using System.Text.Json;
using Beko.Agents.Application.Homes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Beko.Agents.Infrastructure.Persistence.Mongo;

/// <summary>Writes accepted device commands to the Mongo <c>device_events</c> collection.</summary>
internal sealed class MongoDeviceEventStore(IMongoDatabase database) : IDeviceEventStore
{
    public const string CollectionName = "device_events";

    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    private readonly IMongoCollection<BsonDocument> _collection = database.GetCollection<BsonDocument>(CollectionName);

    public Task AddAsync(DeviceEventRecord deviceEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deviceEvent);

        var document = new BsonDocument
        {
            { "commandId", deviceEvent.CommandId },
            { "homeId", deviceEvent.HomeId },
            { "deviceId", deviceEvent.DeviceId },
            { "source", deviceEvent.Source },
            { "memberId", deviceEvent.MemberId },
            { "command", ToBson(new { type = deviceEvent.CommandType, value = deviceEvent.CommandValue }) },
            { "before", ToBson(deviceEvent.Before) },
            { "after", ToBson(deviceEvent.After) },
            { "atUtc", deviceEvent.AtUtc },
            { "trace_id", deviceEvent.TraceId is null ? BsonNull.Value : new BsonString(deviceEvent.TraceId) },
            { "span_id", deviceEvent.SpanId is null ? BsonNull.Value : new BsonString(deviceEvent.SpanId) },
        };

        return _collection.InsertOneAsync(document, options: null, cancellationToken);
    }

    private static BsonDocument ToBson<T>(T value) => BsonDocument.Parse(JsonSerializer.Serialize(value, JsonOptions));
}

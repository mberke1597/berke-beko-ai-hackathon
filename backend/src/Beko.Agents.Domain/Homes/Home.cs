using System.Text.Json.Serialization;

namespace Beko.Agents.Domain.Homes;

public sealed record Home(string Id, string Name, string City, GeoLocation Location);

public sealed record GeoLocation(double Lat, double Lng);

public sealed record Room(string Id, string Name);

/// <summary>A household member as HomeWhiz knows it. <see cref="Role"/> is <c>owner</c>, <c>member</c> or <c>guest</c>.</summary>
public sealed record HouseholdMember(string Id, string DisplayName, string Role);

/// <summary>One telemetry sample. Room values are set only for air conditioners.</summary>
public sealed record TelemetryPoint(
    DateTime TsUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? RoomTempC,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? HumidityPct,
    double PowerW);

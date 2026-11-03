using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Beko.Agents.Application.Telemetry;

/// <summary>Shared OpenTelemetry instruments for the platform's own spans and metrics.</summary>
public static class BekoTelemetry
{
    public const string SourceName = "Beko.Agents";

    public const string MeterName = "Beko.Agents";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    public static readonly Meter Meter = new(MeterName);
}

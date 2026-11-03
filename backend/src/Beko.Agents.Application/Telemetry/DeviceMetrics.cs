using System.Diagnostics.Metrics;

namespace Beko.Agents.Application.Telemetry;

/// <summary>Device metrics. <c>device_commands</c> is exported to Prometheus as <c>device_commands_total{source,type}</c>.</summary>
public static class DeviceMetrics
{
    public static readonly Counter<long> Commands =
        BekoTelemetry.Meter.CreateCounter<long>("device_commands", unit: "{command}", description: "Accepted device commands by source and type.");
}

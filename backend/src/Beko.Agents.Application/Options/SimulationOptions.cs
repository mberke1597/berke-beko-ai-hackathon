using System.ComponentModel.DataAnnotations;

namespace Beko.Agents.Application.Options;

/// <summary>Device simulator settings (section <c>Simulation</c>).</summary>
public sealed class SimulationOptions
{
    public const string SectionName = "Simulation";

    /// <summary>Simulated seconds per real second (demo: 60).</summary>
    [Range(0.01, 10_000)]
    public double TimeScale { get; set; } = 1;

    /// <summary>Real seconds between simulator ticks.</summary>
    [Range(0.05, 3_600)]
    public double TickSeconds { get; set; } = 5;

    /// <summary>Fixed outdoor temperature until the weather mock exists.</summary>
    [Range(-40, 60)]
    public double OutdoorTempC { get; set; } = 31;
}

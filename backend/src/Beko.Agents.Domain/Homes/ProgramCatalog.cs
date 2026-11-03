namespace Beko.Agents.Domain.Homes;

/// <summary>Washer and dishwasher program profile: default temperature, duration and average power draw.</summary>
public sealed record ProgramProfile(string Name, int DefaultTemperatureC, int DurationMin, double AveragePowerW);

/// <summary>Programs of the demo washer and dishwasher (fictional Beko-style profiles).</summary>
public static class ProgramCatalog
{
    public static readonly IReadOnlyList<ProgramProfile> WashingMachine =
    [
        new("cotton40", 40, 120, 450),
        new("cotton60", 60, 150, 650),
        new("eco40", 40, 180, 300),
        new("synthetic40", 40, 90, 400),
        new("quick30", 30, 30, 500),
    ];

    public static readonly IReadOnlyList<ProgramProfile> Dishwasher =
    [
        new("eco50", 50, 180, 350),
        new("auto", 55, 120, 600),
        new("intensive70", 70, 140, 900),
        new("quick35", 35, 30, 800),
    ];

    public static ProgramProfile? Find(string applianceType, string program)
    {
        var programs = applianceType switch
        {
            ApplianceTypes.WashingMachine => WashingMachine,
            ApplianceTypes.Dishwasher => Dishwasher,
            _ => [],
        };

        return programs.FirstOrDefault(p => string.Equals(p.Name, program, StringComparison.Ordinal));
    }
}

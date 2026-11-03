using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace Beko.Agents.Application.Options;

/// <summary>Tariff, rated power and savings assumptions (section <c>Energy</c>). Nothing about cost is hard-coded elsewhere.</summary>
public sealed class EnergyOptions
{
    public const string SectionName = "Energy";

    /// <summary>Flat tariff in TL per kWh. Used when time-of-use is off and for the monthly estimates.</summary>
    [Range(0.01, 1_000)]
    public double TariffTlPerKwh { get; set; } = 2.7;

    /// <summary>When true, <see cref="TimeOfUseBands"/> price energy by home-local time of day.</summary>
    public bool UseTimeOfUse { get; set; } = true;

    /// <summary>Three-rate tariff (day / peak / night). Must cover 24 hours without overlap when <see cref="UseTimeOfUse"/> is on.</summary>
    public List<TariffBand> TimeOfUseBands { get; } = [];

    /// <summary>Electrical input of the air conditioner at full cooling (kW).</summary>
    [Range(0.05, 50)]
    public double RatedCoolingKw { get; set; } = 1.0;

    /// <summary>Electrical input of the air conditioner at full heating (kW).</summary>
    [Range(0.05, 50)]
    public double RatedHeatingKw { get; set; } = 1.1;

    /// <summary>Energy saved per degree the setpoint moves toward the outdoor temperature (approximation, typically 6 to 8).</summary>
    [Range(0, 30)]
    public double SavingPerDegreePercent { get; set; } = 7;

    /// <summary>Power of dry mode relative to cooling (0 to 1).</summary>
    [Range(0.01, 1)]
    public double DryModeFactor { get; set; } = 0.6;

    /// <summary>Power of fan-only mode relative to cooling (0 to 1).</summary>
    [Range(0.01, 1)]
    public double FanModeFactor { get; set; } = 0.12;

    /// <summary>Cooling setpoint the household "usually" uses without Çeliknaz; baseline for the savings estimate.</summary>
    [Range(16, 30)]
    public double ReferenceCoolSetpointC { get; set; } = 24;

    /// <summary>Heating setpoint the household "usually" uses without Çeliknaz; baseline for the savings estimate.</summary>
    [Range(16, 30)]
    public double ReferenceHeatSetpointC { get; set; } = 22;

    /// <summary>Share of the rated cooling power an air conditioner would have drawn while the home is empty (baseline of "turned off when away").</summary>
    [Range(0, 1)]
    public double AwayDutyFactor { get; set; } = 0.5;

    /// <summary>Home-local time = UTC + this many hours (Istanbul = 3). Tariff bands, routines and the day type use it.</summary>
    [Range(-12, 14)]
    public double HomeUtcOffsetHours { get; set; } = 3;

    /// <summary>Energy per program, keyed <c>{applianceType}.{program}</c>; falls back to the program catalog.</summary>
    public Dictionary<string, ProgramEnergyProfile> ProgramProfiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Estimated monthly kWh per priority mode (<c>saving</c>, <c>balanced</c>, <c>comfort</c>) for the settings screen.</summary>
    public Dictionary<string, double> EstimatedMonthlyKwh { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One tariff band. <see cref="From"/> and <see cref="To"/> are home-local <c>HH:mm</c>; a band may wrap midnight.</summary>
public sealed class TariffBand
{
    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string From { get; set; } = "00:00";

    [Required]
    public string To { get; set; } = "00:00";

    [Range(0.01, 1_000)]
    public double TlPerKwh { get; set; }
}

/// <summary>Energy of one program run.</summary>
public sealed class ProgramEnergyProfile
{
    [Range(0.001, 100)]
    public double Kwh { get; set; }

    [Range(1, 1_440)]
    public int DurationMin { get; set; }
}

/// <summary>Validates <see cref="EnergyOptions"/> including nested bands and profiles.</summary>
public sealed class EnergyOptionsValidator : IValidateOptions<EnergyOptions>
{
    private static readonly string[] Modes = ["saving", "balanced", "comfort"];

    public ValidateOptionsResult Validate(string? name, EnergyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        foreach (var band in options.TimeOfUseBands)
        {
            Collect(band, $"Energy:TimeOfUseBands:{band.Name}", failures);
        }

        foreach (var (key, profile) in options.ProgramProfiles)
        {
            Collect(profile, $"Energy:ProgramProfiles:{key}", failures);
        }

        if (options.UseTimeOfUse)
        {
            if (options.TimeOfUseBands.Count == 0)
            {
                failures.Add("Energy:TimeOfUseBands must contain bands when UseTimeOfUse is true.");
            }
            else if (failures.Count == 0 && !CoversDayExactlyOnce(options.TimeOfUseBands))
            {
                failures.Add("Energy:TimeOfUseBands must cover all 24 hours exactly once.");
            }
        }

        foreach (var mode in Modes)
        {
            if (!options.EstimatedMonthlyKwh.TryGetValue(mode, out var kwh) || kwh <= 0)
            {
                failures.Add($"Energy:EstimatedMonthlyKwh:{mode} must be greater than 0.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Collect(object instance, string path, List<string> failures)
    {
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true))
        {
            failures.AddRange(results.Select(r => $"{path}: {r.ErrorMessage}"));
        }
    }

    private static bool CoversDayExactlyOnce(List<TariffBand> bands)
    {
        var coverage = new int[96];
        foreach (var band in bands)
        {
            if (!TryParseTime(band.From, out var from) || !TryParseTime(band.To, out var to))
            {
                return false;
            }

            for (var slot = (int)(from.TotalMinutes / 15); slot != (int)(to.TotalMinutes / 15); slot = (slot + 1) % 96)
            {
                coverage[slot]++;
            }
        }

        return coverage.All(c => c == 1);
    }

    private static bool TryParseTime(string text, out TimeSpan value) =>
        TimeSpan.TryParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture, out value) && value.TotalMinutes % 15 == 0;
}

using System.Globalization;
using Beko.Agents.Application.Options;
using Beko.Agents.Domain.Homes;

namespace Beko.Agents.Application.Energy;

/// <summary>Prices energy by home-local time of day from <see cref="EnergyOptions"/> and estimates appliance power.</summary>
public sealed class EnergyModel
{
    private const int SlotMinutes = 15;

    private readonly EnergyOptions _options;
    private readonly double[] _slotRates = new double[96];
    private readonly string[] _slotNames = new string[96];

    public EnergyModel(EnergyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        Array.Fill(_slotRates, options.TariffTlPerKwh);
        Array.Fill(_slotNames, "flat");
        if (!options.UseTimeOfUse)
        {
            return;
        }

        foreach (var band in options.TimeOfUseBands)
        {
            var from = ParseSlot(band.From);
            var to = ParseSlot(band.To);
            for (var slot = from; slot != to; slot = (slot + 1) % 96)
            {
                _slotRates[slot] = band.TlPerKwh;
                _slotNames[slot] = band.Name;
            }
        }
    }

    public EnergyOptions Options => _options;

    /// <summary>TL per kWh at a home-local time of day.</summary>
    public double RateAt(TimeSpan localTimeOfDay) => _slotRates[Slot(localTimeOfDay)];

    public string BandAt(TimeSpan localTimeOfDay) => _slotNames[Slot(localTimeOfDay)];

    /// <summary>Cost in TL of <paramref name="kwh"/> drawn evenly over <paramref name="duration"/> starting at <paramref name="localStart"/>.</summary>
    public double CostOf(DateTime localStart, TimeSpan duration, double kwh)
    {
        if (duration <= TimeSpan.Zero)
        {
            return kwh * RateAt(localStart.TimeOfDay);
        }

        var minutes = Math.Max(1, (int)Math.Ceiling(duration.TotalMinutes));
        double weighted = 0;
        for (var i = 0; i < minutes; i++)
        {
            weighted += RateAt(localStart.AddMinutes(i).TimeOfDay);
        }

        return kwh * (weighted / minutes);
    }

    /// <summary>Electrical power of an air conditioner in kW for a mode and setpoint, from rated power and the saving per degree.</summary>
    public double AcKw(string mode, double setpoint)
    {
        switch (mode)
        {
            case "fan":
                return _options.RatedCoolingKw * _options.FanModeFactor;
            case "dry":
                return _options.RatedCoolingKw * _options.DryModeFactor;
            case "heat":
                return _options.RatedHeatingKw * Math.Clamp(1 - (_options.SavingPerDegreePercent / 100 * (_options.ReferenceHeatSetpointC - setpoint)), 0.15, 1.5);
            default:
                return _options.RatedCoolingKw * Math.Clamp(1 - (_options.SavingPerDegreePercent / 100 * (setpoint - _options.ReferenceCoolSetpointC)), 0.15, 1.5);
        }
    }

    /// <summary>Energy profile of a program run: configuration first, then the program catalog (average power times duration).</summary>
    public ProgramEnergyProfile? FindProgram(string applianceType, string program)
    {
        if (_options.ProgramProfiles.TryGetValue($"{applianceType}.{program}", out var configured))
        {
            return configured;
        }

        return ProgramCatalog.Find(applianceType, program) is { } catalog
            ? new ProgramEnergyProfile { Kwh = catalog.AveragePowerW * catalog.DurationMin / 60 / 1000, DurationMin = catalog.DurationMin }
            : null;
    }

    /// <summary>Estimated monthly TL of a priority mode (flat tariff, rounded to 10 TL).</summary>
    public double EstimatedMonthlyTl(string mode) =>
        _options.EstimatedMonthlyKwh.TryGetValue(mode, out var kwh) ? Math.Round(kwh * _options.TariffTlPerKwh / 10) * 10 : 0;

    private static int Slot(TimeSpan timeOfDay) => (int)(timeOfDay.TotalMinutes % 1440 / SlotMinutes) % 96;

    private static int ParseSlot(string text) =>
        (int)(TimeSpan.ParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture).TotalMinutes / SlotMinutes);
}

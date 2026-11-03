using Beko.Agents.Application.Common;
using Beko.Agents.Application.Homes;
using Beko.Agents.Domain.Homes;

namespace Beko.Agents.Application.Energy;

public sealed record DeviceEnergy(string ApplianceId, double TodayKwh, double TodayTl);

/// <summary>Wire shape of <c>GET /api/homes/me/energy</c>.</summary>
public sealed record EnergySummary(
    double TodayKwh,
    double TodayTl,
    double WeekKwh,
    double WeekTl,
    double SavedWeekKwh,
    double SavedWeekTl,
    IReadOnlyList<DeviceEnergy> PerDevice);

/// <summary>
/// Accumulates energy from the simulator's power readings (kWh and TL per appliance and simulated local day) and estimates savings.
/// <para>
/// Savings estimate (documented assumption, only for air conditioners whose last command came from Çeliknaz, source <c>ai</c>):
/// the baseline is the same hours at the household's usual setpoints (<c>EnergyOptions.ReferenceCoolSetpointC</c> / <c>ReferenceHeatSetpointC</c>)
/// and in plain cooling mode. Each degree the setpoint sits toward the outdoor temperature saves <c>SavingPerDegreePercent</c>,
/// dry and fan modes save versus cooling by their factors, and an AI-switched-off air conditioner in an empty home saves
/// <c>AwayDutyFactor x RatedCoolingKw</c>. Savings are never reported negative.
/// </para>
/// Also an observer of the command pipeline to learn who last changed each device.
/// </summary>
public sealed class EnergyTracker(EnergyModel model, IHomeClock clock) : IDeviceCommandObserver
{
    private static readonly TimeSpan MaxSampleGap = TimeSpan.FromHours(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<(string Appliance, DateOnly Day), Bucket> _buckets = [];
    private readonly Dictionary<string, string> _lastSource = new(StringComparer.Ordinal);
    private DateTime? _lastSampleUtc;

    public Task OnCommandAsync(DeviceEventRecord deviceEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deviceEvent);
        lock (_gate)
        {
            _lastSource[deviceEvent.DeviceId] = deviceEvent.Source;
        }

        return Task.CompletedTask;
    }

    /// <summary>Adds the energy used since the previous sample. <paramref name="allAway"/> is true when nobody is home.</summary>
    public void RecordSample(IReadOnlyList<Appliance> appliances, bool allAway)
    {
        ArgumentNullException.ThrowIfNull(appliances);
        var now = clock.UtcNow;
        lock (_gate)
        {
            var previous = _lastSampleUtc;
            _lastSampleUtc = now;
            if (previous is null || now <= previous)
            {
                return;
            }

            var dt = now - previous.Value;
            if (dt > MaxSampleGap)
            {
                dt = MaxSampleGap;
            }

            var hours = dt.TotalHours;
            var local = clock.ToLocal(now);
            var rate = model.RateAt(local.TimeOfDay);
            var day = DateOnly.FromDateTime(local);
            foreach (var appliance in appliances)
            {
                var kwh = appliance.State.PowerW / 1000 * hours;
                var saved = SavedKwh(appliance, kwh, hours, allAway);
                var bucket = BucketOf(appliance.Id, day);
                bucket.Kwh += kwh;
                bucket.Tl += kwh * rate;
                bucket.SavedKwh += saved;
                bucket.SavedTl += saved * rate;
            }

            Prune(day);
        }
    }

    /// <summary>Adds a synthetic day (demo history).</summary>
    public void AddHistory(DateOnly day, string applianceId, double kwh, double tl, double savedKwh, double savedTl)
    {
        lock (_gate)
        {
            var bucket = BucketOf(applianceId, day);
            bucket.Kwh += kwh;
            bucket.Tl += tl;
            bucket.SavedKwh += savedKwh;
            bucket.SavedTl += savedTl;
        }
    }

    public EnergySummary Summary()
    {
        var today = DateOnly.FromDateTime(clock.LocalNow());
        var weekStart = today.AddDays(-6);
        lock (_gate)
        {
            var todays = _buckets.Where(b => b.Key.Day == today).ToList();
            var week = _buckets.Where(b => b.Key.Day >= weekStart && b.Key.Day <= today).Select(b => b.Value).ToList();
            return new EnergySummary(
                Round(todays.Sum(b => b.Value.Kwh)),
                Round(todays.Sum(b => b.Value.Tl)),
                Round(week.Sum(b => b.Kwh)),
                Round(week.Sum(b => b.Tl)),
                Round(week.Sum(b => b.SavedKwh)),
                Round(week.Sum(b => b.SavedTl)),
                [.. todays.OrderBy(b => b.Key.Appliance, StringComparer.Ordinal).Select(b => new DeviceEnergy(b.Key.Appliance, Round(b.Value.Kwh), Round(b.Value.Tl)))]);
        }
    }

    private double SavedKwh(Appliance appliance, double actualKwh, double hours, bool allAway)
    {
        if (appliance.State.Ac is not { } ac || !_lastSource.TryGetValue(appliance.Id, out var source) || source != "ai")
        {
            return 0;
        }

        var options = model.Options;
        if (!appliance.State.Power)
        {
            return allAway ? options.RatedCoolingKw * options.AwayDutyFactor * hours : 0;
        }

        switch (ac.Mode)
        {
            case "dry":
                return actualKwh * ((1 / options.DryModeFactor) - 1);
            case "fan":
                return actualKwh * ((1 / options.FanModeFactor) - 1);
            default:
                var degrees = ac.Mode == "heat" ? options.ReferenceHeatSetpointC - ac.Setpoint : ac.Setpoint - options.ReferenceCoolSetpointC;
                var fraction = Math.Clamp(options.SavingPerDegreePercent / 100 * degrees, 0, 0.5);
                return actualKwh * ((1 / (1 - fraction)) - 1);
        }
    }

    private Bucket BucketOf(string applianceId, DateOnly day)
    {
        if (!_buckets.TryGetValue((applianceId, day), out var bucket))
        {
            bucket = new Bucket();
            _buckets[(applianceId, day)] = bucket;
        }

        return bucket;
    }

    private void Prune(DateOnly today)
    {
        var oldest = today.AddDays(-14);
        foreach (var key in _buckets.Keys.Where(k => k.Day < oldest).ToList())
        {
            _buckets.Remove(key);
        }
    }

    private static double Round(double value) => Math.Round(value, 2);

    private sealed class Bucket
    {
        public double Kwh { get; set; }

        public double Tl { get; set; }

        public double SavedKwh { get; set; }

        public double SavedTl { get; set; }
    }
}

/// <summary>Wire shape of <c>GET /api/homes/me/cheapest-time</c>.</summary>
public sealed record CheapestTime(DateTime StartAtUtc, double CostTl, double NowCostTl, double SavingTl);

/// <summary>Finds the cheapest tariff window for a washer or dishwasher program before a deadline.</summary>
public sealed class CheapestTimeService(IHomeWhizClient homeWhiz, EnergyModel model, IHomeClock clock)
{
    private static readonly TimeSpan Step = TimeSpan.FromMinutes(15);

    /// <summary>Returns <c>null</c> when the appliance is unknown; throws <see cref="ArgumentException"/> for an unknown program.</summary>
    public async Task<CheapestTime?> FindAsync(string applianceId, string? programId, DateTime? deadlineUtc, CancellationToken cancellationToken)
    {
        var appliance = await homeWhiz.GetApplianceAsync(applianceId, cancellationToken);
        if (appliance is null)
        {
            return null;
        }

        var program = programId ?? appliance.State.Program?.Program
            ?? throw new ArgumentException("This appliance has no program; pass programId.", nameof(programId));
        var profile = model.FindProgram(appliance.Type, program)
            ?? throw new ArgumentException($"Unknown program '{program}' for {appliance.Type}.", nameof(programId));

        var now = clock.UtcNow;
        var duration = TimeSpan.FromMinutes(profile.DurationMin);
        var deadline = deadlineUtc ?? now.AddHours(24);
        var latestStart = deadline - duration;

        double CostAt(DateTime startUtc) => model.CostOf(clock.ToLocal(startUtc), duration, profile.Kwh);

        var nowCost = CostAt(now);
        var best = now;
        var bestCost = nowCost;
        for (var start = now + Step; start <= latestStart; start += Step)
        {
            var cost = CostAt(start);
            if (cost < bestCost - 1e-9)
            {
                best = start;
                bestCost = cost;
            }
        }

        return new CheapestTime(
            DateTime.SpecifyKind(best, DateTimeKind.Utc),
            Math.Round(bestCost, 2),
            Math.Round(nowCost, 2),
            Math.Round(nowCost - bestCost, 2));
    }
}

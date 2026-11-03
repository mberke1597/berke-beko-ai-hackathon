using System.Globalization;
using Beko.Agents.Application.Energy;
using Beko.Agents.Domain.Homes;

namespace Beko.Agents.Application.Comfort;

/// <summary>A person whose comfort counts. <see cref="SensitivityOffset"/> positive means "feels colder than average".</summary>
public sealed record MemberComfort(string MemberId, double SensitivityOffset, string Clothing, double SetpointBiasC);

/// <summary>Everything the policy needs to judge one air conditioner.</summary>
public sealed record ComfortSituation(
    double RoomTempC,
    double HumidityPct,
    string AcMode,
    double Setpoint,
    string Fan,
    bool AcRunning,
    double OutdoorTempC,
    double OutdoorHumidityPct,
    string Season,
    int LocalHour,
    double BandPmv,
    IReadOnlyList<MemberComfort> Members,
    SetpointRange SetpointRange,
    double RateTlPerKwh,
    double DryHumidityThresholdPct);

public sealed record ComfortAlternative(string Action, double? Setpoint, double CostPerHourTl);

public sealed record ComfortRecommendation(
    string Action,
    double? Setpoint,
    string? Mode,
    string? Fan,
    string Reason,
    double CostPerHourTl,
    IReadOnlyList<ComfortAlternative> Alternatives);

public sealed record MemberPmv(string MemberId, double PmvAtCompromise);

public sealed record ComfortConflict(bool Present, double? CompromiseSetpoint, IReadOnlyList<MemberPmv> PerMember);

public sealed record ComfortAssessment(
    double RoomTempC,
    double HumidityPct,
    double Pmv,
    string Feeling,
    bool InBand,
    double BandPmv,
    ComfortRecommendation Recommendation,
    ComfortConflict Conflict);

/// <summary>Action names of a <see cref="ComfortRecommendation"/>.</summary>
public static class ComfortActions
{
    public const string None = "none";
    public const string Window = "window";
    public const string Fan = "fan";
    public const string Dry = "dry";
    public const string Setpoint = "setpoint";
}

/// <summary>
/// Decision policy on top of <see cref="ComfortEngine"/>: is everyone inside the priority-mode band, and if not, which is
/// the cheapest action that gets them there (none/window, fan, dry, smallest setpoint change)? With several members the
/// setpoint is the compromise that minimises the worst |PMV|.
/// </summary>
public sealed class ComfortPolicy(EnergyModel energy)
{
    private const double ConflictSpreadC = 1.0;

    private static readonly MemberComfort ReferencePerson = new(string.Empty, 0, "normal", 0);

    public ComfortAssessment Evaluate(ComfortSituation s)
    {
        ArgumentNullException.ThrowIfNull(s);

        var persons = s.Members.Count > 0 ? s.Members : [ReferencePerson];
        var met = ComfortEngine.MetForHour(s.LocalHour);
        var runningAirSpeed = ComfortEngine.AirSpeedMs(s.Fan, running: true);
        var currentAirSpeed = ComfortEngine.AirSpeedMs(s.Fan, s.AcRunning);

        var now = persons.Select(p => (Person: p, Pmv: MemberPmvAt(p, s, s.RoomTempC, s.HumidityPct, currentAirSpeed, met))).ToList();
        var driver = now.MaxBy(x => Math.Abs(x.Pmv));
        var inBand = Math.Abs(driver.Pmv) <= s.BandPmv;
        var cooling = driver.Pmv > 0;

        var conflict = BuildConflict(s, persons, met, runningAirSpeed);

        ComfortRecommendation recommendation;
        double conflictReferenceSetpoint = conflict.CompromiseSetpoint ?? s.RoomTempC;
        if (inBand)
        {
            var cost = s.AcRunning ? energy.AcKw(s.AcMode, s.Setpoint) * s.RateTlPerKwh : 0;
            recommendation = new ComfortRecommendation(
                ComfortActions.None,
                null,
                null,
                null,
                $"Oda {Num(s.RoomTempC)} °C ve konfor bandının içinde, değişiklik gerekmiyor.",
                Round(cost),
                []);
        }
        else
        {
            recommendation = Recommend(s, persons, met, runningAirSpeed, cooling, driver.Pmv, conflict);
            conflictReferenceSetpoint = recommendation.Setpoint ?? conflictReferenceSetpoint;
        }

        var perMember = conflict.Present || persons.Count > 1
            ? conflict.PerMember
            : [.. persons.Select(p => new MemberPmv(p.MemberId, Round(MemberPmvAt(p, s, conflictReferenceSetpoint, s.HumidityPct, runningAirSpeed, met))))];

        return new ComfortAssessment(
            s.RoomTempC,
            s.HumidityPct,
            Round(driver.Pmv),
            ComfortEngine.Feeling(driver.Pmv),
            inBand,
            s.BandPmv,
            recommendation,
            conflict with { PerMember = perMember });
    }

    private ComfortRecommendation Recommend(
        ComfortSituation s,
        IReadOnlyList<MemberComfort> persons,
        double met,
        double runningAirSpeed,
        bool cooling,
        double driverPmv,
        ComfortConflict conflict)
    {
        var feelingText = FeelingText(ComfortEngine.Feeling(driverPmv));
        var alternatives = new List<ComfortAlternative>();
        ComfortRecommendation? chosen = null;

        // 1. Do nothing / open a window when the outdoor air itself is comfortable and helps.
        var helps = cooling ? s.OutdoorTempC < s.RoomTempC : s.OutdoorTempC > s.RoomTempC;
        if (helps
            && ComfortEngine.OutdoorAirIsAdaptivelyComfortable(s.OutdoorTempC)
            && WorstAbs(persons, s, s.OutdoorTempC, s.OutdoorHumidityPct, ComfortEngine.AirSpeedMs("low", true), met) <= s.BandPmv)
        {
            chosen = new ComfortRecommendation(
                ComfortActions.Window,
                null,
                null,
                null,
                $"Dışarısı {Num(s.OutdoorTempC)} °C ve ferah; klimayı çalıştırmadan pencere açmak yeterli.",
                0,
                []);
        }

        // 2. Fan only (cooling).
        if (cooling)
        {
            foreach (var level in new[] { "medium", "high" })
            {
                if (WorstAbs(persons, s, s.RoomTempC, s.HumidityPct, ComfortEngine.AirSpeedMs(level, true), met) > s.BandPmv)
                {
                    continue;
                }

                var option = new ComfortRecommendation(
                    ComfortActions.Fan,
                    null,
                    "fan",
                    level,
                    $"Oda {Num(s.RoomTempC)} °C ve {feelingText}. Fanı {FanText(level)} seviyeye almak sizi konfor bandına sokar ve soğutmaktan çok daha ucuzdur.",
                    Round(energy.AcKw("fan", s.Setpoint) * s.RateTlPerKwh),
                    []);
                Collect(ref chosen, alternatives, option);
                break;
            }
        }

        // 3. Dry mode when humid: feels cooler and costs less than a lower setpoint.
        if (cooling
            && s.HumidityPct >= s.DryHumidityThresholdPct
            && WorstAbs(persons, s, s.RoomTempC - 0.5, Math.Max(40, s.HumidityPct - 10), ComfortEngine.AirSpeedMs("low", true), met) <= s.BandPmv)
        {
            var option = new ComfortRecommendation(
                ComfortActions.Dry,
                null,
                "dry",
                "low",
                $"Nem %{s.HumidityPct.ToString("0", CultureInfo.InvariantCulture)} yüksek. Nem alma modu serinlik hissi verir ve düşük sıcaklık ayarından daha ucuzdur.",
                Round(energy.AcKw("dry", s.Setpoint) * s.RateTlPerKwh),
                []);
            Collect(ref chosen, alternatives, option);
        }

        // 4. Setpoint: smallest change that gets everyone into the band (or the compromise).
        var setpointOption = SetpointOption(s, persons, met, runningAirSpeed, cooling, conflict, feelingText);
        if (chosen is null)
        {
            var step = Math.Max(s.SetpointRange.Step, 0.5);
            var gentler = Clamp(s, setpointOption.Setpoint!.Value + (cooling ? -1 : 1), step);
            if (Math.Abs(gentler - setpointOption.Setpoint.Value) > 1e-9)
            {
                alternatives.Add(new ComfortAlternative(ComfortActions.Setpoint, gentler, Round(energy.AcKw(setpointOption.Mode!, gentler) * s.RateTlPerKwh)));
            }

            return setpointOption with { Alternatives = alternatives };
        }

        alternatives.Add(new ComfortAlternative(ComfortActions.Setpoint, setpointOption.Setpoint, setpointOption.CostPerHourTl));
        return chosen with { Alternatives = alternatives };
    }

    private ComfortRecommendation SetpointOption(
        ComfortSituation s,
        IReadOnlyList<MemberComfort> persons,
        double met,
        double airSpeed,
        bool cooling,
        ComfortConflict conflict,
        string feelingText)
    {
        var grid = Grid(s.SetpointRange);
        var feasible = grid.Where(t => WorstAbs(persons, s, t, s.HumidityPct, airSpeed, met) <= s.BandPmv).ToList();

        double target;
        string why;
        if (persons.Count > 1 && conflict.Present && conflict.CompromiseSetpoint is { } compromise)
        {
            target = compromise;
            why = $"Birden fazla kişi var; en kötü hissi en aza indiren uzlaşma {Num(target)} °C.";
        }
        else if (feasible.Count > 0)
        {
            // Most economical point of the band: highest setpoint for cooling, lowest for heating.
            target = cooling ? feasible.Max() : feasible.Min();
            why = $"{Num(target)} °C konfor bandının sınırına yakın olduğu için en tasarruflu seçenek.";
        }
        else
        {
            target = MinimaxSetpoint(grid, persons, s, airSpeed, met, s.RoomTempC);
            why = $"Bandın tamamına girilemiyor; en yakın nokta {Num(target)} °C.";
        }

        if (persons.Count > 0)
        {
            target = Clamp(s, target + persons.Average(p => p.SetpointBiasC), Math.Max(s.SetpointRange.Step, 0.5));
        }

        var mode = cooling ? "cool" : "heat";
        var modeText = cooling ? "soğutma" : "ısıtma";
        return new ComfortRecommendation(
            ComfortActions.Setpoint,
            target,
            mode,
            s.Fan,
            $"Oda {Num(s.RoomTempC)} °C ve {feelingText}. {modeText} modunda {Num(target)} °C önerilir: {why}",
            Round(energy.AcKw(mode, target) * s.RateTlPerKwh),
            []);
    }

    private static ComfortConflict BuildConflict(ComfortSituation s, IReadOnlyList<MemberComfort> persons, double met, double airSpeed)
    {
        if (persons.Count < 2)
        {
            return new ComfortConflict(false, null, []);
        }

        var grid = Grid(s.SetpointRange);
        var ideals = persons
            .Select(p => grid.MinBy(t => Math.Abs(MemberPmvAt(p, s, t, s.HumidityPct, airSpeed, met))))
            .ToList();
        var compromise = MinimaxSetpoint(grid, persons, s, airSpeed, met, s.RoomTempC);
        var present = ideals.Max() - ideals.Min() >= ConflictSpreadC;
        return new ComfortConflict(
            present,
            compromise,
            [.. persons.Select(p => new MemberPmv(p.MemberId, Round(MemberPmvAt(p, s, compromise, s.HumidityPct, airSpeed, met))))]);
    }

    private static void Collect(ref ComfortRecommendation? chosen, List<ComfortAlternative> alternatives, ComfortRecommendation option)
    {
        if (chosen is null)
        {
            chosen = option;
        }
        else
        {
            alternatives.Add(new ComfortAlternative(option.Action, option.Setpoint, option.CostPerHourTl));
        }
    }

    private static double MinimaxSetpoint(IReadOnlyList<double> grid, IReadOnlyList<MemberComfort> persons, ComfortSituation s, double airSpeed, double met, double near)
    {
        var scored = grid.Select(t => (T: t, Worst: WorstAbs(persons, s, t, s.HumidityPct, airSpeed, met))).ToList();
        var best = scored.Min(x => x.Worst);
        return scored.Where(x => x.Worst <= best + 1e-9).MinBy(x => Math.Abs(x.T - near)).T;
    }

    private static double WorstAbs(IReadOnlyList<MemberComfort> persons, ComfortSituation s, double tempC, double humidityPct, double airSpeed, double met) =>
        persons.Max(p => Math.Abs(MemberPmvAt(p, s, tempC, humidityPct, airSpeed, met)));

    private static double MemberPmvAt(MemberComfort person, ComfortSituation s, double tempC, double humidityPct, double airSpeed, double met) =>
        ComfortEngine.Calculate(tempC, humidityPct, airSpeed, met, ComfortEngine.Clo(s.Season, person.Clothing)).Pmv - person.SensitivityOffset;

    private static List<double> Grid(SetpointRange range)
    {
        var step = Math.Max(range.Step, 0.5);
        var grid = new List<double>();
        for (var t = range.Min; t <= range.Max + 1e-9; t += step)
        {
            grid.Add(Math.Round(t, 1));
        }

        return grid;
    }

    private static double Clamp(ComfortSituation s, double value, double step) =>
        Math.Round(Math.Clamp(Math.Round(value / step) * step, s.SetpointRange.Min, s.SetpointRange.Max), 1);

    private static double Round(double value) => Math.Round(value, 2);

    private static string Num(double value) => value.ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',');

    private static string FanText(string level) => level == "high" ? "yüksek" : "orta";

    private static string FeelingText(string feeling) =>
        feeling switch
        {
            "cold" => "çok soğuk",
            "cool" => "serin",
            "slightly_cool" => "hafif serin",
            "neutral" => "nötr",
            "slightly_warm" => "hafif sıcak",
            "warm" => "sıcak",
            _ => "çok sıcak",
        };
}

namespace Beko.Agents.Application.Comfort;

/// <summary>Inputs of the PMV model. Mean radiant temperature is taken equal to the air temperature by the callers.</summary>
public readonly record struct PmvInput(double AirTempC, double RadiantTempC, double RelativeAirSpeedMs, double HumidityPct, double Met, double Clo);

public readonly record struct PmvResult(double Pmv, double Ppd);

/// <summary>
/// Thermal comfort maths, pure and deterministic.
/// <para>
/// PMV/PPD follow ISO 7730 (Annex D computer program, Fanger's heat balance), the same algorithm as ASHRAE 55 and
/// the CBE <c>pythermalcomfort</c> reference implementation (checked against its documented examples, see tests).
/// Sign convention: <b>positive PMV = warm/hot, negative = cool/cold</b> (scale -3 cold ... +3 hot).
/// </para>
/// <para>
/// Formulas (SI, <c>m</c> = met * 58.15 W/m2, no external work):
/// pa = rh * 10 * exp(16.6536 - 4030.183 / (ta + 235)) Pa; icl = 0.155 clo;
/// fcl = 1 + 1.29 icl (icl &lt;= 0.078) else 1.05 + 0.645 icl; hc = max(12.1 sqrt(vr), 2.38 |tcl - ta|^0.25), with the clothing
/// surface temperature tcl found by iteration (tolerance 0.00015); losses hl1..hl6 (skin diffusion, sweating, latent and dry
/// respiration, radiation, convection); ts = 0.303 exp(-0.036 m) + 0.028; PMV = ts (m - hl1 ... hl6);
/// PPD = 100 - 95 exp(-0.03353 PMV^4 - 0.2179 PMV^2).
/// </para>
/// <para>
/// Relative air speed (ASHRAE 55 / pythermalcomfort <c>v_relative</c>): vr = v + 0.3 (met - 1) for met &gt; 1, else v.
/// Adaptive model (ASHRAE 55): T_comfort = 0.31 * T_out_mean + 17.8, 80 % acceptability limits +-3.5 K, valid for T_out 10 to 33.5 C.
/// </para>
/// </summary>
public static class ComfortEngine
{
    private const double MetToWatt = 58.15;

    private const double Tolerance = 0.00015;

    private const int MaxIterations = 150;

    public static PmvResult Calculate(PmvInput input)
    {
        var ta = input.AirTempC;
        var m = input.Met * MetToWatt;
        var mw = m; // external work is zero for domestic activities
        var pa = input.HumidityPct * 10 * Math.Exp(16.6536 - (4030.183 / (ta + 235)));
        var icl = 0.155 * input.Clo;
        var fcl = icl <= 0.078 ? 1 + (1.29 * icl) : 1.05 + (0.645 * icl);
        var hcf = 12.1 * Math.Sqrt(input.RelativeAirSpeedMs);
        var taa = ta + 273;
        var tra = input.RadiantTempC + 273;

        var tcla = taa + ((35.5 - ta) / ((3.5 * icl) + 0.1));
        var p1 = icl * fcl;
        var p2 = p1 * 3.96;
        var p3 = p1 * 100;
        var p4 = p1 * taa;
        var p5 = (308.7 - (0.028 * mw)) + (p2 * Math.Pow(tra / 100, 4));
        var xn = tcla / 100;
        var xf = tcla / 50;
        var hc = hcf;
        var iterations = 0;
        while (Math.Abs(xn - xf) > Tolerance)
        {
            xf = (xf + xn) / 2;
            var hcn = 2.38 * Math.Pow(Math.Abs((100 * xf) - taa), 0.25);
            hc = Math.Max(hcf, hcn);
            xn = (p5 + (p4 * hc) - (p2 * Math.Pow(xf, 4))) / (100 + (p3 * hc));
            if (++iterations > MaxIterations)
            {
                break;
            }
        }

        var tcl = (100 * xn) - 273;
        var hl1 = 3.05 * 0.001 * (5733 - (6.99 * mw) - pa);
        var hl2 = mw > MetToWatt ? 0.42 * (mw - MetToWatt) : 0;
        var hl3 = 1.7 * 0.00001 * m * (5867 - pa);
        var hl4 = 0.0014 * m * (34 - ta);
        var hl5 = 3.96 * fcl * (Math.Pow(xn, 4) - Math.Pow(tra / 100, 4));
        var hl6 = fcl * hc * (tcl - ta);
        var ts = (0.303 * Math.Exp(-0.036 * m)) + 0.028;
        var pmv = ts * (mw - hl1 - hl2 - hl3 - hl4 - hl5 - hl6);
        var ppd = 100 - (95 * Math.Exp((-0.03353 * Math.Pow(pmv, 4)) - (0.2179 * pmv * pmv)));
        return new PmvResult(pmv, ppd);
    }

    /// <summary>PMV for a sitting or standing person with air speed <paramref name="airSpeedMs"/> (body movement added) and MRT = air temperature.</summary>
    public static PmvResult Calculate(double airTempC, double humidityPct, double airSpeedMs, double met, double clo) =>
        Calculate(new PmvInput(airTempC, airTempC, RelativeAirSpeed(airSpeedMs, met), humidityPct, met, clo));

    public static double RelativeAirSpeed(double airSpeedMs, double met) =>
        met > 1 ? airSpeedMs + (0.3 * (met - 1)) : airSpeedMs;

    /// <summary>ASHRAE 55 adaptive comfort temperature for a mean outdoor temperature.</summary>
    public static double AdaptiveComfortTempC(double outdoorMeanC) => (0.31 * outdoorMeanC) + 17.8;

    /// <summary>True when an indoor temperature equal to the outdoor one is inside the 80 % adaptive limits (and the model's validity range).</summary>
    public static bool OutdoorAirIsAdaptivelyComfortable(double outdoorMeanC) =>
        outdoorMeanC is >= 10 and <= 33.5 && Math.Abs(outdoorMeanC - AdaptiveComfortTempC(outdoorMeanC)) <= 3.5;

    /// <summary>Seven-point sensation label for a PMV.</summary>
    public static string Feeling(double pmv) =>
        pmv switch
        {
            < -2.5 => "cold",
            < -1.5 => "cool",
            < -0.5 => "slightly_cool",
            <= 0.5 => "neutral",
            <= 1.5 => "slightly_warm",
            <= 2.5 => "warm",
            _ => "hot",
        };

    /// <summary>Air speed at the occupant for an AC fan level. Not running = still indoor air.</summary>
    public static double AirSpeedMs(string? fanLevel, bool running) =>
        !running
            ? 0.1
            : fanLevel switch
            {
                "low" => 0.2,
                "medium" => 0.35,
                "high" => 0.5,
                _ => 0.15,
            };

    /// <summary>Metabolic rate by home-local hour: sleeping 0.7, seated evening 1.0, light activity 1.2.</summary>
    public static double MetForHour(int localHour) =>
        localHour switch
        {
            >= 23 or < 6 => 0.7,
            >= 18 => 1.0,
            _ => 1.2,
        };

    /// <summary>Clothing insulation: the season default, shifted for the member's clothing choice (light: -0.2, heavy: +0.4).</summary>
    public static double Clo(string season, string clothing)
    {
        var normal = season switch
        {
            "summer" => 0.5,
            "winter" => 1.0,
            _ => 0.7,
        };
        return clothing switch
        {
            "light" => Math.Max(0.3, normal - 0.2),
            "heavy" => normal + 0.4,
            _ => normal,
        };
    }
}

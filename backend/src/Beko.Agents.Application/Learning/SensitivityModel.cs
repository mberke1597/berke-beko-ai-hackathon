namespace Beko.Agents.Application.Learning;

/// <summary>
/// Learns how much colder (positive offset) or warmer (negative) a member feels than the average person.
/// The member's PMV is <c>PMV_iso - offset</c>. A sensation of cold (-1) means the real PMV was at or below -0.5,
/// fine (0) means within +-0.5, hot (+1) at or above +0.5; the offset moves toward making the prediction match.
/// </summary>
public static class SensitivityModel
{
    public const double LearningRate = 0.5;

    public const double MaxOffset = 1.5;

    /// <summary>Weight of a single answer on the setpoint bias (alpha of the contract).</summary>
    public const double BiasRate = 0.3;

    public const double MaxBiasC = 3;

    public static double Update(double offset, double pmvBase, int sensation)
    {
        var predicted = pmvBase - offset;
        var (low, high) = sensation switch
        {
            < 0 => (double.NegativeInfinity, -0.5),
            > 0 => (0.5, double.PositiveInfinity),
            _ => (-0.5, 0.5),
        };
        var error = predicted - Math.Clamp(predicted, low, high);
        return Math.Clamp(offset + (LearningRate * error), -MaxOffset, MaxOffset);
    }

    /// <summary>Setpoint bias after an answer: cold wants warmer (+), hot wants cooler (-), in cooling and heating alike.</summary>
    public static double UpdateBias(double bias, int sensation) =>
        Math.Clamp(bias + (BiasRate * -sensation), -MaxBiasC, MaxBiasC);

    /// <summary>Setpoint bias after an override: <c>bias += 0.3 x delta</c>, delta being user setpoint minus AI setpoint.</summary>
    public static double UpdateBiasFromOverride(double bias, double deltaC) =>
        Math.Clamp(bias + (BiasRate * deltaC), -MaxBiasC, MaxBiasC);
}

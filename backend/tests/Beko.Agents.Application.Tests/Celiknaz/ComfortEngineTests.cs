using Beko.Agents.Application.Comfort;

namespace Beko.Agents.Application.Tests.Celiknaz;

/// <summary>
/// Reference values: the documented examples of the CBE <c>pythermalcomfort</c> <c>pmv_ppd_iso</c> function
/// (ISO 7730 algorithm), plus the ISO 7730 table D.1 style case 22 C / 60 % / 0.1 m/s / 1.2 met / 0.5 clo.
/// </summary>
public sealed class ComfortEngineTests
{
    [Fact]
    public void Pmv_CbeExample_25C_Rh50_Met14_Clo05_WithRelativeAirSpeed()
    {
        var vr = ComfortEngine.RelativeAirSpeed(0.1, 1.4);
        var result = ComfortEngine.Calculate(new PmvInput(25, 25, vr, 50, 1.4, 0.5));

        Assert.Equal(0.22, vr, 3);
        Assert.Equal(0.17, result.Pmv, 2);
        Assert.Equal(5.6, result.Ppd, 1);
    }

    [Fact]
    public void Pmv_CbeExample_TwoAirTemperatures()
    {
        var cool = ComfortEngine.Calculate(new PmvInput(22, 25, 0.1, 50, 1.4, 0.5));
        var warm = ComfortEngine.Calculate(new PmvInput(25, 25, 0.1, 50, 1.4, 0.5));

        Assert.Equal(0.0, cool.Pmv, 1);
        Assert.Equal(5.0, cool.Ppd, 0);
        Assert.Equal(0.41, warm.Pmv, 2);
        Assert.Equal(8.5, warm.Ppd, 1);
    }

    [Fact]
    public void Pmv_Iso7730TableD1_Case_22C_Rh60()
    {
        var result = ComfortEngine.Calculate(new PmvInput(22, 22, 0.1, 60, 1.2, 0.5));

        Assert.Equal(-0.75, result.Pmv, 1);
        Assert.Equal(17, result.Ppd, 0);
    }

    [Fact]
    public void Pmv_SignConvention_PositiveWarm_NegativeCold()
    {
        Assert.True(ComfortEngine.Calculate(30, 50, 0.1, 1.2, 0.5).Pmv > 1.5);
        Assert.True(ComfortEngine.Calculate(16, 50, 0.1, 1.2, 0.5).Pmv < -2);
        Assert.Equal("hot", ComfortEngine.Feeling(2.9));
        Assert.Equal("cold", ComfortEngine.Feeling(-3));
        Assert.Equal("neutral", ComfortEngine.Feeling(0.2));
    }

    [Fact]
    public void Pmv_MoreAirSpeedAndLessClothing_FeelCooler()
    {
        var still = ComfortEngine.Calculate(27, 50, 0.1, 1.0, 0.5).Pmv;
        var fan = ComfortEngine.Calculate(27, 50, 0.5, 1.0, 0.5).Pmv;
        var light = ComfortEngine.Calculate(27, 50, 0.1, 1.0, 0.3).Pmv;

        Assert.True(fan < still);
        Assert.True(light < still);
    }

    [Fact]
    public void Adaptive_Model_MatchesAshrae55Formula()
    {
        Assert.Equal(26.0, ComfortEngine.AdaptiveComfortTempC(26.45), 1);
        Assert.True(ComfortEngine.OutdoorAirIsAdaptivelyComfortable(24));
        Assert.False(ComfortEngine.OutdoorAirIsAdaptivelyComfortable(35));
        Assert.False(ComfortEngine.OutdoorAirIsAdaptivelyComfortable(5));
    }

    [Fact]
    public void Clo_FollowsSeasonAndClothingChoice()
    {
        Assert.Equal(0.5, ComfortEngine.Clo("summer", "normal"));
        Assert.Equal(1.0, ComfortEngine.Clo("winter", "normal"));
        Assert.True(ComfortEngine.Clo("autumn", "heavy") > ComfortEngine.Clo("autumn", "normal"));
        Assert.True(ComfortEngine.Clo("autumn", "light") < ComfortEngine.Clo("autumn", "normal"));
    }

    [Theory]
    [InlineData(3, 0.7)]
    [InlineData(10, 1.2)]
    [InlineData(20, 1.0)]
    [InlineData(23, 0.7)]
    public void Met_ByHourOfDay(int hour, double expected) => Assert.Equal(expected, ComfortEngine.MetForHour(hour));
}

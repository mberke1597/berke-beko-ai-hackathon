using Beko.Agents.Application.Agents.Llm;

using Beko.Agents.TestSupport;

namespace Beko.Agents.Application.Tests.Agents;

public sealed class CostCalculatorTests
{
    private readonly CostCalculator _calculator = new(TestSettings.Pricing());

    [Fact]
    public void Sonnet_IncludesInputOutputAndCacheTokens()
    {
        // 1000 in * 2 + 500 out * 10 + 2000 cache read * 0.10 + 400 cache write * 2.5, all per million
        var cost = _calculator.Calculate(TestSettings.DefaultModel, new LlmUsage(1000, 500, 2000, 400));

        Assert.Equal((2000m + 5000m + 200m + 1000m) / 1_000_000m, cost);
    }

    [Fact]
    public void Haiku_UsesItsOwnPrices()
    {
        var cost = _calculator.Calculate(TestSettings.FastModel, new LlmUsage(1_000_000, 1_000_000, 1_000_000, 1_000_000));

        Assert.Equal(0.1m + 0.5m + 0.01m + 0.125m, cost);
    }

    [Fact]
    public void UnknownModel_CostsNothing()
    {
        Assert.Equal(0m, _calculator.Calculate("some-other-model", new LlmUsage(1000, 1000, 0, 0)));
    }

    [Fact]
    public void Usage_AddsUp()
    {
        var total = new LlmUsage(1, 2, 3, 4) + new LlmUsage(10, 20, 30, 40);

        Assert.Equal(new LlmUsage(11, 22, 33, 44), total);
    }
}

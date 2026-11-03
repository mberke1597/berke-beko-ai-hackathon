using Beko.Agents.Application.Options;
using Microsoft.Extensions.Options;

namespace Beko.Agents.Application.Agents.Llm;

public interface ICostCalculator
{
    /// <summary>USD cost of <paramref name="usage"/> on <paramref name="model"/>; 0 when the model has no price.</summary>
    decimal Calculate(string model, LlmUsage usage);
}

/// <summary>Cost from <see cref="PricingOptions"/> (USD per million tokens, including cache reads and writes).</summary>
public sealed class CostCalculator(IOptions<PricingOptions> pricing) : ICostCalculator
{
    private const decimal TokensPerMillion = 1_000_000m;

    public decimal Calculate(string model, LlmUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        if (!pricing.Value.Models.TryGetValue(model, out var price) || price is null)
        {
            return 0m;
        }

        var cost = (usage.InputTokens * price.InputPerMTok)
            + (usage.OutputTokens * price.OutputPerMTok)
            + (usage.CacheReadTokens * price.CacheReadPerMTok)
            + (usage.CacheWriteTokens * price.CacheWritePerMTok);
        return cost / TokensPerMillion;
    }
}

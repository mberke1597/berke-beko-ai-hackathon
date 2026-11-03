using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Beko.Agents.Application.Options;

/// <summary>Model prices in USD per million tokens (section <c>Pricing</c>).</summary>
public sealed class PricingOptions
{
    public const string SectionName = "Pricing";

    /// <summary>Prices keyed by model id, e.g. <c>claude-sonnet-5-5</c>.</summary>
    public Dictionary<string, ModelPricing> Models { get; } = new(StringComparer.Ordinal);
}

/// <summary>USD per million tokens for one model.</summary>
public sealed class ModelPricing
{
    [Range(typeof(decimal), "0", "1000000")]
    public decimal InputPerMTok { get; set; }

    [Range(typeof(decimal), "0", "1000000")]
    public decimal OutputPerMTok { get; set; }

    [Range(typeof(decimal), "0", "1000000")]
    public decimal CacheReadPerMTok { get; set; }

    [Range(typeof(decimal), "0", "1000000")]
    public decimal CacheWritePerMTok { get; set; }
}

/// <summary>Requires at least one model and validates every entry (data annotations do not recurse into dictionaries).</summary>
public sealed class PricingOptionsValidator : IValidateOptions<PricingOptions>
{
    public ValidateOptionsResult Validate(string? name, PricingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Models.Count == 0)
        {
            return ValidateOptionsResult.Fail("Pricing:Models must contain at least one model.");
        }

        var failures = new List<string>();
        foreach (var (model, pricing) in options.Models)
        {
            if (pricing is null)
            {
                failures.Add($"Pricing:Models:{model} is missing.");
                continue;
            }

            var results = new List<ValidationResult>();
            if (!Validator.TryValidateObject(pricing, new ValidationContext(pricing), results, validateAllProperties: true))
            {
                failures.AddRange(results.Select(r => $"Pricing:Models:{model}: {r.ErrorMessage}"));
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

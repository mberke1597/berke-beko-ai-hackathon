using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Beko.Agents.Application.Options;

/// <summary>Anthropic API settings (section <c>Anthropic</c>).</summary>
public sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    /// <summary>Secret. Required in Production; may be empty in Development and tests.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Model used by agents.</summary>
    [Required]
    public string DefaultModel { get; set; } = "claude-sonnet-5-5";

    /// <summary>Model used for guardrails and classification.</summary>
    [Required]
    public string FastModel { get; set; } = "claude-haiku-5-5";

    [Range(1, 128_000)]
    public int MaxTokens { get; set; } = 4096;

    /// <summary>API base URL. Set explicitly so an unrelated <c>ANTHROPIC_BASE_URL</c> variable is not picked up.</summary>
    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://api.anthropic.com";
}

/// <summary>Validates the secret part of <see cref="AnthropicOptions"/>.</summary>
public sealed class AnthropicOptionsValidator(bool requireSecrets) : IValidateOptions<AnthropicOptions>
{
    public ValidateOptionsResult Validate(string? name, AnthropicOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return requireSecrets && string.IsNullOrWhiteSpace(options.ApiKey)
            ? ValidateOptionsResult.Fail("Anthropic:ApiKey is required.")
            : ValidateOptionsResult.Success;
    }
}

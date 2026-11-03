using System.ComponentModel.DataAnnotations;
using Beko.Agents.Application.Options;

namespace Beko.Agents.Application.Tests.Options;

public sealed class OptionsValidationTests
{
    private const string ValidSigningKey = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void AnthropicOptions_Defaults_PassDataAnnotations()
    {
        Assert.Empty(ValidateAnnotations(new AnthropicOptions()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(128_001)]
    public void AnthropicOptions_MaxTokensOutOfRange_Fails(int maxTokens)
    {
        var errors = ValidateAnnotations(new AnthropicOptions { MaxTokens = maxTokens });

        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(AnthropicOptions.MaxTokens)));
    }

    [Fact]
    public void AnthropicOptions_EmptyDefaultModel_Fails()
    {
        var errors = ValidateAnnotations(new AnthropicOptions { DefaultModel = "" });

        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(AnthropicOptions.DefaultModel)));
    }

    [Fact]
    public void AnthropicValidator_MissingApiKey_FailsOnlyWhenSecretsRequired()
    {
        var options = new AnthropicOptions { ApiKey = "" };

        Assert.True(new AnthropicOptionsValidator(requireSecrets: true).Validate(null, options).Failed);
        Assert.True(new AnthropicOptionsValidator(requireSecrets: false).Validate(null, options).Succeeded);
    }

    [Fact]
    public void AnthropicValidator_WithApiKey_Succeeds()
    {
        var options = new AnthropicOptions { ApiKey = "test-key" };

        Assert.True(new AnthropicOptionsValidator(requireSecrets: true).Validate(null, options).Succeeded);
    }

    [Fact]
    public void JwtValidator_MissingSigningKey_FailsOnlyWhenSecretsRequired()
    {
        var options = new JwtOptions();

        Assert.True(new JwtOptionsValidator(requireSecrets: true).Validate(null, options).Failed);
        Assert.True(new JwtOptionsValidator(requireSecrets: false).Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void JwtValidator_ShortSigningKey_AlwaysFails(bool requireSecrets)
    {
        var options = new JwtOptions { SigningKey = "too-short" };

        Assert.True(new JwtOptionsValidator(requireSecrets).Validate(null, options).Failed);
    }

    [Fact]
    public void JwtValidator_LongEnoughSigningKey_Succeeds()
    {
        var options = new JwtOptions { SigningKey = ValidSigningKey };

        Assert.True(new JwtOptionsValidator(requireSecrets: true).Validate(null, options).Succeeded);
    }

    [Fact]
    public void MongoOptions_MissingConnectionString_Fails()
    {
        var errors = ValidateAnnotations(new MongoOptions());

        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(MongoOptions.ConnectionString)));
    }

    [Fact]
    public void MongoOptions_WithConnectionString_Succeeds()
    {
        Assert.Empty(ValidateAnnotations(new MongoOptions { ConnectionString = "mongodb://localhost:27017" }));
    }

    [Fact]
    public void AppOptions_EmptyVersion_Fails()
    {
        var errors = ValidateAnnotations(new AppOptions { Version = "" });

        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(AppOptions.Version)));
    }

    [Fact]
    public void PricingValidator_NoModels_Fails()
    {
        Assert.True(new PricingOptionsValidator().Validate(null, new PricingOptions()).Failed);
    }

    [Fact]
    public void PricingValidator_NegativePrice_Fails()
    {
        var options = new PricingOptions();
        options.Models["claude-sonnet-5-5"] = new ModelPricing { InputPerMTok = -1m, OutputPerMTok = 10m };

        var result = new PricingOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("claude-sonnet-5-5", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void PricingValidator_ValidModels_Succeeds()
    {
        var options = new PricingOptions();
        options.Models["claude-sonnet-5-5"] = new ModelPricing
        {
            InputPerMTok = 2m,
            OutputPerMTok = 10m,
            CacheReadPerMTok = 0.2m,
            CacheWritePerMTok = 2.5m,
        };

        Assert.True(new PricingOptionsValidator().Validate(null, options).Succeeded);
    }

    private static List<ValidationResult> ValidateAnnotations(object options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }
}

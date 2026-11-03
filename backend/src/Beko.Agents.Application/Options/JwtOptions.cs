using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.Extensions.Options;

namespace Beko.Agents.Application.Options;

/// <summary>JWT settings (section <c>Jwt</c>).</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public const int MinSigningKeyBytes = 32;

    /// <summary>Secret, at least 32 bytes. Required in Production; may be empty in Development and tests.</summary>
    public string SigningKey { get; set; } = string.Empty;

    [Required]
    public string Issuer { get; set; } = "beko-agents";

    [Required]
    public string Audience { get; set; } = "beko-agents";
}

/// <summary>Validates the secret part of <see cref="JwtOptions"/>.</summary>
public sealed class JwtOptionsValidator(bool requireSecrets) : IValidateOptions<JwtOptions>
{
    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrEmpty(options.SigningKey))
        {
            return requireSecrets
                ? ValidateOptionsResult.Fail("Jwt:SigningKey is required.")
                : ValidateOptionsResult.Success;
        }

        return Encoding.UTF8.GetByteCount(options.SigningKey) < JwtOptions.MinSigningKeyBytes
            ? ValidateOptionsResult.Fail($"Jwt:SigningKey must be at least {JwtOptions.MinSigningKeyBytes} bytes.")
            : ValidateOptionsResult.Success;
    }
}

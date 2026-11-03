using System.ComponentModel.DataAnnotations;

namespace Beko.Agents.Application.Options;

/// <summary>MongoDB settings (section <c>Mongo</c>).</summary>
public sealed class MongoOptions
{
    public const string SectionName = "Mongo";

    /// <summary>May contain credentials; set it through environment variables outside Development.</summary>
    [Required]
    public string ConnectionString { get; set; } = string.Empty;

    [Required]
    public string Database { get; set; } = "beko";
}

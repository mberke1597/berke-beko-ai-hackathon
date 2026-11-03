using System.ComponentModel.DataAnnotations;

namespace Beko.Agents.Application.Options;

/// <summary>HomeWhiz connection (section <c>HomeWhiz</c>). Today the base URL points to our own mock endpoints.</summary>
public sealed class HomeWhizOptions
{
    public const string SectionName = "HomeWhiz";

    /// <summary>Absolute base URL, e.g. <c>http://localhost:5080/api/mock/homewhiz</c>.</summary>
    [Required]
    [Url]
    public string BaseUrl { get; set; } = string.Empty;
}

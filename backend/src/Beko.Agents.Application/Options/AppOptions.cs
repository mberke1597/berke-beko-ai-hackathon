using System.ComponentModel.DataAnnotations;

namespace Beko.Agents.Application.Options;

/// <summary>General application settings (section <c>App</c>).</summary>
public sealed class AppOptions
{
    public const string SectionName = "App";

    /// <summary>Product name shown by <c>/api/version</c>.</summary>
    public const string ProductName = "Beko Agent Platform";

    [Required]
    public string Version { get; set; } = "0.1.0";

    [Required]
    public string Commit { get; set; } = "unknown";

    /// <summary>Base URL used to build Grafana trace links, e.g. <c>/grafana</c>.</summary>
    [Required]
    public string GrafanaBaseUrl { get; set; } = "/grafana";
}

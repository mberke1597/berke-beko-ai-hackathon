using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Beko.Agents.Application.Homes;
using Beko.Agents.Domain.Homes;

namespace Beko.Agents.Infrastructure.HomeWhiz;

/// <summary>HTTP implementation of <see cref="IHomeWhizClient"/>; the base address comes from <c>HomeWhiz:BaseUrl</c>.</summary>
public sealed class HomeWhizHttpClient(HttpClient http) : IHomeWhizClient
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    public async Task<IReadOnlyList<HomeSummary>> GetHomesAsync(CancellationToken cancellationToken) =>
        await GetAsync<List<HomeSummary>>("homes", cancellationToken) ?? [];

    public async Task<HomeDetails?> GetHomeAsync(string homeId, CancellationToken cancellationToken) =>
        await GetAsync<HomeDetails>($"homes/{Uri.EscapeDataString(homeId)}", cancellationToken);

    public async Task<IReadOnlyList<Appliance>?> GetAppliancesAsync(string homeId, CancellationToken cancellationToken) =>
        await GetAsync<List<Appliance>>($"homes/{Uri.EscapeDataString(homeId)}/appliances", cancellationToken);

    public async Task<Appliance?> GetApplianceAsync(string applianceId, CancellationToken cancellationToken) =>
        await GetAsync<Appliance>($"appliances/{Uri.EscapeDataString(applianceId)}", cancellationToken);

    public async Task<IReadOnlyList<TelemetryPoint>?> GetTelemetryAsync(string applianceId, DateTime? fromUtc, DateTime? toUtc, CancellationToken cancellationToken)
    {
        var query = new List<string>();
        if (fromUtc is { } from)
        {
            query.Add($"fromUtc={Uri.EscapeDataString(FormatUtc(from))}");
        }

        if (toUtc is { } to)
        {
            query.Add($"toUtc={Uri.EscapeDataString(FormatUtc(to))}");
        }

        var path = $"appliances/{Uri.EscapeDataString(applianceId)}/telemetry";
        if (query.Count > 0)
        {
            path += "?" + string.Join('&', query);
        }

        return await GetAsync<List<TelemetryPoint>>(path, cancellationToken);
    }

    public async Task<CommandResult?> SendCommandAsync(string applianceId, ApplianceCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        using var response = await http.PostAsJsonAsync(
            new Uri($"appliances/{Uri.EscapeDataString(applianceId)}/commands", UriKind.Relative),
            new { type = command.Type, value = command.Value },
            JsonOptions,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            throw new ApplianceCommandRejectedException(await ReadRejectionAsync(response, cancellationToken));
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CommandResult>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("HomeWhiz returned an empty command result.");
    }

    private static async Task<CommandRejection> ReadRejectionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = problem.RootElement;
        return new CommandRejection(
            ReadString(root, "code") ?? CommandRejectionCodes.InvalidValue,
            ReadString(root, "title") ?? "Komut reddedildi",
            ReadString(root, "detail") ?? "HomeWhiz rejected the command.");
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string FormatUtc(DateTime value) =>
        DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        using var response = await http.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
    }
}

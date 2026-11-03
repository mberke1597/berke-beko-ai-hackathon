using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Beko.Agents.Infrastructure.Telemetry;

internal static class TelemetryExtensions
{
    private const string DefaultServiceName = "beko-backend";

    /// <summary>Activity sources of the official Anthropic C# SDK.</summary>
    private const string AnthropicSdkSources = "com.anthropic.sdk.csharp*";

    /// <summary>Built-in .NET runtime metrics (GC, thread pool, JIT, ...).</summary>
    private const string RuntimeMeterName = "System.Runtime";

    /// <summary>
    /// Traces, metrics and logs via OpenTelemetry. OTLP export is enabled only when the standard
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> variable is set, so local runs and tests stay quiet.
    /// </summary>
    public static IServiceCollection AddBekoTelemetry(this IServiceCollection services, IConfiguration configuration, string serviceVersion)
    {
        var serviceName = configuration["OTEL_SERVICE_NAME"];
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            serviceName = DefaultServiceName;
        }

        var otel = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName, serviceVersion: serviceVersion))
            .WithTracing(tracing => tracing
                .AddSource($"{BekoTelemetry.SourceName}*")
                .AddSource(AnthropicSdkSources)
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter(RuntimeMeterName)
                .AddMeter(BekoTelemetry.MeterName))
            .WithLogging(configureBuilder: null, configureOptions: options =>
            {
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
            });

        if (!string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        return services;
    }
}

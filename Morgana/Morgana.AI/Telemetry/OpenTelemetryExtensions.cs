using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Morgana.AI.Telemetry;

/// <summary>
/// Extension methods for registering Morgana OpenTelemetry instrumentation.
/// </summary>
/// <remarks>
/// <para><strong>Exporters:</strong></para>
/// <list type="bullet">
/// <item><term>otlp</term><description>Sends traces and metrics via gRPC: compatible with Jaeger, Grafana Tempo, Azure Monitor, Datadog, ...</description></item>
/// <item><term>console</term><description>Writes traces to stdout: useful for development</description></item>
/// </list>
/// </remarks>
public static class TelemetryExtensions
{
    /// <summary>
    /// Registers Morgana OpenTelemetry tracing and metrics with the ASP.NET Core DI container.
    /// Respects the <c>Morgana:OpenTelemetry:Enabled</c> flag — when false, this is a no-op.
    /// </summary>
    /// <param name="services">The service collection to configure</param>
    /// <param name="configuration">Application configuration (appsettings.json)</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddMorganaOpenTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        IConfigurationSection section = configuration.GetSection("Morgana:OpenTelemetry");

        // OTel is globally flaggable
        if (!section.GetValue("Enabled", false))
            return services;

        // OTel is also locally flaggable at exporter level
        string serviceName = section.GetValue("ServiceName", "Morgana")!;
        ExporterConfig[] exporters = section.GetSection("Exporters").Get<ExporterConfig[]>() ?? [];
        ExporterConfig? otlpExporter = exporters.FirstOrDefault(e => e.Name.Equals("otlp", StringComparison.OrdinalIgnoreCase) && e.Enabled);
        bool consoleEnabled = exporters.Any(e => e.Name.Equals("console", StringComparison.OrdinalIgnoreCase) && e.Enabled);
        if (otlpExporter is null && !consoleEnabled)
            return services;

        // Tracing pipeline produces detailed journey logging, which is always meaningful 
        OpenTelemetryBuilder otel = services
            .AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing
                    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName))
                    .AddSource(MorganaTelemetry.Source.Name)
                    .AddAspNetCoreInstrumentation();

                if (otlpExporter is not null)
                    tracing.AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otlpExporter.Endpoint ?? "http://localhost:4317"));
                if (consoleEnabled)
                    tracing.AddConsoleExporter();
            });

        // Metrics pipeline produces aggregated data like counters and histograms, which is only meaningful
        // when consumed by an OTLP-compatible backend — skip entirely when OTLP is not configured
        if (otlpExporter is not null)
        {
            otel.WithMetrics(metrics =>
            {
                metrics
                    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName))
                    .AddMeter(MorganaTelemetry.MorganaMeter.Name)
                    .AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otlpExporter.Endpoint ?? "http://localhost:4317"));
            });
        }

        return services;
    }

    private record ExporterConfig(string Name, bool Enabled, string? Endpoint = null);
}

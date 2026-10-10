using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Morgana.AI;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Morgana.AI.Extensions;

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
public static class OpenTelemetryExtensions
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

        // A host that does not enable telemetry pays for none of it: no source is listened to and no exporter is built.
        if (!section.GetValue("Enabled", false))
            return services;

        // Exporters are switched on one by one: the OTLP entry carries the endpoint, the console one is a development aid.
        string serviceName = section.GetValue("ServiceName", Constants.Morgana)!;
        ExporterConfig[] exporters = section.GetSection("Exporters").Get<ExporterConfig[]>() ?? [];
        ExporterConfig? otlpExporter = exporters.FirstOrDefault(e => e.Name.Equals("otlp", StringComparison.OrdinalIgnoreCase) && e.Enabled);
        bool consoleEnabled = exporters.Any(e => e.Name.Equals("console", StringComparison.OrdinalIgnoreCase) && e.Enabled);
        // Telemetry enabled with every exporter off would collect spans nobody reads.
        if (otlpExporter is null && !consoleEnabled)
            return services;

        // Traces follow a conversation's journey and are meaningful with any exporter: both the framework's spans and the LLM client's are collected.
        OpenTelemetryBuilder otel = services
            .AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing
                    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName))
                    .AddSource(Telemetry.Source.Name)
                    .AddSource(Telemetry.LLMChatClientSourceName) // MEAI OpenTelemetryChatClient activity source
                    .AddAspNetCoreInstrumentation();

                // The conventional local collector address applies when the entry names no endpoint.
                if (otlpExporter is not null)
                    tracing.AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otlpExporter.Endpoint ?? "http://localhost:4317"));
                // The console exporter writes spans to stdout for development.
                if (consoleEnabled)
                    tracing.AddConsoleExporter();
            });

        // Counters and histograms are aggregates that only an OTLP-compatible backend can consume: without one no meter is collected.
        if (otlpExporter is not null)
        {
            otel.WithMetrics(metrics =>
            {
                metrics
                    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName))
                    .AddMeter(Telemetry.MorganaMeter.Name)
                    .AddMeter(Telemetry.LLMChatClientSourceName) // MEAI OpenTelemetryChatClient meter
                    .AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otlpExporter.Endpoint ?? "http://localhost:4317"));
            });
        }

        return services;
    }

    /// <summary>One entry of <c>Morgana:OpenTelemetry:Exporters</c>: an exporter by name, its switch and its optional endpoint.</summary>
    private record ExporterConfig(string Name, bool Enabled, string? Endpoint = null);
}

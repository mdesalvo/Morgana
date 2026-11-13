using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Morgana.AI.Extensions;

/// <summary>Registers Morgana's traces and metrics with the exporters that <c>Morgana:OpenTelemetry</c> enables.</summary>
public static class OpenTelemetryExtensions
{
    /// <summary>Address an OTLP exporter sends to when its entry declares none: the collector's own default gRPC port.</summary>
    private const string DefaultOTLPEndpoint = "http://localhost:4317";

    /// <summary>Wires tracing and metrics to the enabled exporters; registers nothing when telemetry or every exporter is off.</summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">The application configuration holding <c>Morgana:OpenTelemetry</c>.</param>
    public static IServiceCollection AddMorganaOpenTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Read once, at registration: an operator changing telemetry restarts the host.
        IConfigurationSection section = configuration.GetSection("Morgana:OpenTelemetry");

        // Telemetry off leaves the host without any OpenTelemetry pipeline at all.
        if (!section.GetValue("Enabled", false))
            return services;

        // The name a backend groups this installation under, so two installations sharing a collector stay apart.
        string serviceName = section.GetValue("ServiceName", Constants.Morgana)!;

        // Each exporter is switched on by its own entry: otlp sends to a collector, console writes to stdout.
        ExporterConfig[] exporters = section.GetSection("Exporters").Get<ExporterConfig[]>() ?? [];
        ExporterConfig? otlpExporter = exporters.FirstOrDefault(exporter => exporter.Name.Equals("otlp", StringComparison.OrdinalIgnoreCase) && exporter.Enabled);
        bool consoleEnabled = exporters.Any(exporter => exporter.Name.Equals("console", StringComparison.OrdinalIgnoreCase) && exporter.Enabled);

        // With no exporter enabled nothing would leave the process, so nothing is collected either.
        if (otlpExporter is null && !consoleEnabled)
            return services;

        // Traces follow one turn through the pipeline and read well on either exporter.
        OpenTelemetryBuilder otel = services
            .AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                // Morgana's own spans, the model calls of every tier and the HTTP request each turn links to.
                tracing
                    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName))
                    .AddSource(Telemetry.Source.Name)
                    .AddSource(Telemetry.LLMChatClientSourceName)
                    .AddAspNetCoreInstrumentation();

                // A collector such as Jaeger, Tempo or Azure Monitor receives them at its declared endpoint or the default port.
                if (otlpExporter is not null)
                    tracing.AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otlpExporter.Endpoint ?? DefaultOTLPEndpoint));

                // The console prints them for a developer watching the host's output.
                if (consoleEnabled)
                    tracing.AddConsoleExporter();
            });

        // Counters and histograms mean something only aggregated by a backend, so the console never gets them.
        if (otlpExporter is not null)
        {
            // Morgana's own meter beside the token usage the model calls of every tier report.
            otel.WithMetrics(metrics =>
            {
                metrics
                    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName))
                    .AddMeter(Telemetry.MorganaMeter.Name)
                    .AddMeter(Telemetry.LLMChatClientSourceName)
                    .AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otlpExporter.Endpoint ?? DefaultOTLPEndpoint));
            });
        }

        // Handed back so Program.cs goes on registering on the same collection.
        return services;
    }

    /// <summary>One entry of <c>Morgana:OpenTelemetry:Exporters</c>.</summary>
    /// <param name="Name">The exporter, <c>otlp</c> or <c>console</c>.</param>
    /// <param name="Enabled">Whether the exporter is on.</param>
    /// <param name="Endpoint">The collector's address, read by the otlp exporter alone.</param>
    private record ExporterConfig(string Name, bool Enabled, string? Endpoint = null);
}
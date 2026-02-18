using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Morgana.Framework.Telemetry;

/// <summary>
/// Extension methods for registering Morgana OpenTelemetry instrumentation.
/// </summary>
/// <remarks>
/// <para><strong>Exporters:</strong></para>
/// <list type="bullet">
/// <item><term>otlp</term><description>OTLP gRPC — compatible with Jaeger, Grafana Tempo, Azure Monitor, Datadog, Honeycomb</description></item>
/// <item><term>console</term><description>Writes traces to stdout — useful for development</description></item>
/// <item><term>none</term><description>No exporter — OTel SDK is registered but produces no output</description></item>
/// </list>
/// </remarks>
public static class TelemetryExtensions
{
    /// <summary>
    /// Registers Morgana OpenTelemetry tracing with the ASP.NET Core DI container.
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

        if (!section.GetValue("Enabled", false))
            return services;

        services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                string serviceName = section.GetValue("ServiceName", "Morgana")!;

                tracing.SetResourceBuilder(
                    ResourceBuilder.CreateDefault()
                        .AddService(serviceName))
                        // Register the Morgana ActivitySource
                        .AddSource(MorganaTelemetry.Source.Name)
                        // Include inbound HTTP requests (ConversationController endpoints)
                        .AddAspNetCoreInstrumentation();

                ExporterConfig[] exporters = section.GetSection("Exporters").Get<ExporterConfig[]>() ?? [];

                // For Production environment, where OTLP-enabled platforms consume Morgana activities (e.g: Jaeger, ...)
                if (exporters.FirstOrDefault(e => e.Name.Equals("otlp", StringComparison.OrdinalIgnoreCase)) is { Enabled:true } otlpConfig)
                    tracing.AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otlpConfig.Endpoint ?? "http://localhost:4317"));

                // For Development environment, where console quickly shows to developers what's happening
                if (exporters.Any(e => e.Name.Equals("console", StringComparison.OrdinalIgnoreCase) && e.Enabled))
                    tracing.AddConsoleExporter();
            });

        return services;
    }

    private record ExporterConfig(string Name, bool Enabled, string? Endpoint = null);
}
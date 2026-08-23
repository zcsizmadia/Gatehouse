using Gatehouse.Configuration;
using Gatehouse.Diagnostics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Gatehouse.Server.Infrastructure;

/// <summary>
/// Wires OpenTelemetry tracing and metrics.
/// </summary>
internal static class TelemetryExtensions
{
    /// <summary>Registers the tracer and meter providers.</summary>
    public static IHostApplicationBuilder AddGatehouseTelemetry(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        TelemetryOptions telemetry = builder.Configuration
            .GetSection(GatehouseOptions.SectionName)
            .GetSection(nameof(GatehouseOptions.Telemetry))
            .Get<TelemetryOptions>() ?? new TelemetryOptions();

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: telemetry.ServiceName,
                    serviceVersion: GatehouseVersion.Informational,
                    serviceInstanceId: Environment.MachineName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(GatehouseTelemetry.ActivitySourceName)
                    .AddAspNetCoreInstrumentation(o =>
                    {
                        // Health and metrics endpoints are polled every few seconds forever.
                        // Tracing them buries the inference traces an operator actually came
                        // to look at, and costs money in every hosted backend.
                        o.Filter = httpContext => !IsInfrastructurePath(httpContext.Request.Path);
                    })
                    .AddHttpClientInstrumentation();

                if (!string.IsNullOrWhiteSpace(telemetry.OtlpEndpoint))
                {
                    tracing.AddOtlpExporter(o => o.Endpoint = new Uri(telemetry.OtlpEndpoint));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(GatehouseTelemetry.MeterName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                if (!string.IsNullOrWhiteSpace(telemetry.OtlpEndpoint))
                {
                    metrics.AddOtlpExporter(o => o.Endpoint = new Uri(telemetry.OtlpEndpoint));
                }
            });

        return builder;
    }

    private static bool IsInfrastructurePath(PathString path) =>
        path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/metrics", StringComparison.OrdinalIgnoreCase);
}

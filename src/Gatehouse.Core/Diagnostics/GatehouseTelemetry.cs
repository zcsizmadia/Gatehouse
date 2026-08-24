using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Gatehouse.Diagnostics;

/// <summary>
/// The single <see cref="ActivitySource"/> and <see cref="Meter"/> Gatehouse emits from.
/// </summary>
/// <remarks>
/// <para>
/// Gatehouse follows the OpenTelemetry <em>GenAI semantic conventions</em> rather than
/// inventing attribute names. The reason is practical rather than dogmatic: a customer who
/// already has a Grafana or Datadog dashboard for LLM traffic should see Gatehouse data
/// appear in it without writing a single mapping rule. Custom names would make the
/// telemetry technically present and operationally useless.
/// </para>
/// <para>
/// The source and meter are static and never disposed. That is the documented pattern for
/// library-level instrumentation: their lifetime is the process, and disposing them would
/// silently stop telemetry for anything still running.
/// </para>
/// </remarks>
public static class GatehouseTelemetry
{
    /// <summary>The name to enable in an OpenTelemetry tracer provider.</summary>
    public const string ActivitySourceName = "Gatehouse";

    /// <summary>The name to enable in an OpenTelemetry meter provider.</summary>
    public const string MeterName = "Gatehouse";

    private static readonly string Version =
        typeof(GatehouseTelemetry).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    /// <summary>Spans for inference requests passing through the gateway.</summary>
    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName, Version);

    /// <summary>Instruments for inference requests passing through the gateway.</summary>
    public static Meter Meter { get; } = new(MeterName, Version);

    /// <summary>
    /// Token consumption, per request. Tagged with the token kind so that input and output
    /// tokens can be costed at their different rates.
    /// </summary>
    public static Histogram<long> TokenUsage { get; } = Meter.CreateHistogram<long>(
        name: "gen_ai.client.token.usage",
        unit: "{token}",
        description: "Number of input and output tokens used.");

    /// <summary>End-to-end duration of an inference request.</summary>
    public static Histogram<double> OperationDuration { get; } = Meter.CreateHistogram<double>(
        name: "gen_ai.client.operation.duration",
        unit: "s",
        description: "GenAI operation duration.");

    /// <summary>
    /// Time from accepting a streamed request to flushing its first chunk.
    /// </summary>
    /// <remarks>
    /// This is the number that determines whether a streamed response feels responsive, and
    /// it is the one a buffering bug destroys while total duration stays flat. It is
    /// measured separately for exactly that reason.
    /// </remarks>
    public static Histogram<double> TimeToFirstChunk { get; } = Meter.CreateHistogram<double>(
        name: "gatehouse.stream.time_to_first_chunk",
        unit: "s",
        description: "Time from request acceptance to the first streamed chunk being flushed.");

    /// <summary>Requests rejected by Gatehouse before any upstream call was made.</summary>
    public static Counter<long> RequestsRejected { get; } = Meter.CreateCounter<long>(
        name: "gatehouse.requests.rejected",
        unit: "{request}",
        description: "Requests rejected by Gatehouse without reaching a provider.");

    /// <summary>
    /// Requests that were served by a fallback route rather than the one they asked for.
    /// </summary>
    /// <remarks>
    /// The alert to build on this is not "greater than zero" — a fallback firing is the
    /// feature working. It is a sustained rate, which says the primary provider is unhealthy
    /// and nobody has been told, because from the callers' point of view nothing broke.
    /// </remarks>
    public static Counter<long> RouteFallbacks { get; } = Meter.CreateCounter<long>(
        name: "gatehouse.route.fallbacks",
        unit: "{request}",
        description: "Requests served by a fallback route after the primary route failed.");

    /// <summary>Calls not attempted because the upstream's circuit was open.</summary>
    public static Counter<long> CircuitBreakerRejections { get; } = Meter.CreateCounter<long>(
        name: "gatehouse.circuit_breaker.rejections",
        unit: "{request}",
        description: "Upstream calls skipped because the circuit for that upstream was open.");

    /// <summary>
    /// OpenTelemetry GenAI semantic-convention attribute names.
    /// </summary>
    /// <remarks>
    /// Spelled out as constants so that a convention change is a one-file edit with a
    /// compiler-checked call site list, rather than a search for string literals.
    /// </remarks>
    public static class Attributes
    {
        /// <summary>The provider family, for example <c>openai</c> or <c>anthropic</c>.</summary>
        public const string System = "gen_ai.system";

        /// <summary>The operation, which for Gatehouse is always <c>chat</c> in Phase 0.</summary>
        public const string OperationName = "gen_ai.operation.name";

        /// <summary>The model the caller asked for.</summary>
        public const string RequestModel = "gen_ai.request.model";

        /// <summary>The model that actually answered.</summary>
        public const string ResponseModel = "gen_ai.response.model";

        /// <summary>Requested sampling temperature.</summary>
        public const string RequestTemperature = "gen_ai.request.temperature";

        /// <summary>Requested nucleus sampling cutoff.</summary>
        public const string RequestTopP = "gen_ai.request.top_p";

        /// <summary>Requested maximum output tokens.</summary>
        public const string RequestMaxTokens = "gen_ai.request.max_tokens";

        /// <summary>Why generation stopped.</summary>
        public const string ResponseFinishReasons = "gen_ai.response.finish_reasons";

        /// <summary>Prompt tokens consumed.</summary>
        public const string UsageInputTokens = "gen_ai.usage.input_tokens";

        /// <summary>Completion tokens produced.</summary>
        public const string UsageOutputTokens = "gen_ai.usage.output_tokens";

        /// <summary>Whether the token counts came from the provider or from estimation.</summary>
        public const string TokenKind = "gen_ai.token.type";

        /// <summary>The error class, when the operation failed.</summary>
        public const string ErrorType = "error.type";

        /// <summary>The Gatehouse provider key that served the request.</summary>
        public const string GatehouseProvider = "gatehouse.provider";

        /// <summary>The configured alias the caller routed through.</summary>
        public const string GatehouseRouteAlias = "gatehouse.route.alias";

        /// <summary>The upstream model name, which for Azure OpenAI is the deployment.</summary>
        public const string GatehouseUpstreamModel = "gatehouse.upstream.model";

        /// <summary>
        /// How many fallback links were traversed: 1 means the first fallback answered.
        /// </summary>
        public const string GatehouseFallbackDepth = "gatehouse.route.fallback_depth";

        /// <summary>Whether the response was streamed.</summary>
        public const string GatehouseStreamed = "gatehouse.streamed";
    }

    /// <summary>Values for <see cref="Attributes.OperationName"/>.</summary>
    public static class Operations
    {
        /// <summary>A chat completion.</summary>
        public const string Chat = "chat";
    }

    /// <summary>Values for <see cref="Attributes.TokenKind"/>.</summary>
    public static class TokenKinds
    {
        /// <summary>Prompt tokens.</summary>
        public const string Input = "input";

        /// <summary>Completion tokens.</summary>
        public const string Output = "output";
    }
}

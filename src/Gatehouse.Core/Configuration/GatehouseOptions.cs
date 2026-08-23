
namespace Gatehouse.Configuration;

/// <summary>
/// The root of the Gatehouse configuration surface.
/// </summary>
/// <remarks>
/// Configuration is a first-class product surface rather than an implementation detail.
/// The Phase 2 admin UI is required to have <em>config-as-code parity</em>: anything the UI
/// can change must be expressible in a file that lives in source control. That constraint
/// starts here, so the file format is designed to be reviewed in a pull request.
/// </remarks>
public sealed class GatehouseOptions
{
    /// <summary>The configuration section Gatehouse binds from.</summary>
    public const string SectionName = "Gatehouse";

    /// <summary>
    /// Upstream providers, keyed by the name routes refer to them by.
    /// </summary>
    public IDictionary<string, ProviderOptions> Providers { get; init; } =
        new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Model routes, keyed by the alias callers put in the <c>model</c> request field.
    /// </summary>
    public IDictionary<string, ModelRouteOptions> Models { get; init; } =
        new Dictionary<string, ModelRouteOptions>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where request records are persisted.</summary>
    public StoreOptions Store { get; init; } = new();

    /// <summary>Observability configuration.</summary>
    public TelemetryOptions Telemetry { get; init; } = new();
}

/// <summary>Configuration for one upstream provider.</summary>
public sealed class ProviderOptions
{
    /// <summary>
    /// The provider implementation to use, matching <c>IChatProvider.Name</c> — for example
    /// <c>openai-compatible</c>.
    /// </summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>The upstream base address, including any path prefix.</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// A literal API key. Provided for local development only.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="ApiKeyEnvironmentVariable"/>, or managed identity where the
    /// provider supports it. A key committed to a configuration file is a key in your git
    /// history, and Gatehouse logs a warning at startup when this property is used.
    /// </remarks>
    public string? ApiKey { get; init; }

    /// <summary>
    /// The name of an environment variable holding the API key. Takes precedence over
    /// <see cref="ApiKey"/> when both are set.
    /// </summary>
    public string? ApiKeyEnvironmentVariable { get; init; }

    /// <summary>
    /// How long to wait for the upstream to respond.
    /// </summary>
    /// <remarks>
    /// This bounds the whole non-streamed call. For streamed calls it bounds the wait for
    /// response headers only: a long generation is not a stalled one, and applying a total
    /// timeout to a stream would cut off exactly the slow, expensive completions that
    /// callers most want to finish.
    /// </remarks>
    public int TimeoutSeconds { get; init; } = 100;

    /// <summary>Extra headers sent on every upstream request.</summary>
    public IDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether to expose this provider's native API verbatim under
    /// <c>/passthrough/{provider}/</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off by default, and it should stay off unless you need it. Passthrough forwards the
    /// request body untouched, which means Gatehouse cannot read the token counts and cannot
    /// price the call — those requests are recorded as <em>unmetered</em> and will show up in
    /// a chargeback report as a known gap rather than as zero cost.
    /// </para>
    /// <para>
    /// It exists because some provider features have no OpenAI-compatible expression, and the
    /// alternative to an audited escape hatch is applications quietly bypassing the gateway
    /// altogether. An unmetered request Gatehouse knows about beats a metered one it never
    /// saw. Gatehouse logs a warning at startup naming every provider this is enabled for.
    /// </para>
    /// </remarks>
    public bool AllowPassthrough { get; init; }
}

/// <summary>Configuration for one model alias.</summary>
public sealed class ModelRouteOptions
{
    /// <summary>The key in <see cref="GatehouseOptions.Providers"/> that serves this alias.</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>
    /// The model identifier to send upstream. Defaults to the alias itself, which is the
    /// common case when the caller already names a real model.
    /// </summary>
    public string? UpstreamModel { get; init; }

    /// <summary>
    /// Aliases to try in order if this route fails retryably. Fallback execution lands in
    /// Phase 1; the field is defined here so the configuration format does not have to
    /// change underneath early adopters.
    /// </summary>
    public IList<string> Fallbacks { get; init; } = [];
}

/// <summary>Where Gatehouse persists request records.</summary>
public sealed class StoreOptions
{
    /// <summary>
    /// The SQLite connection string.
    /// </summary>
    /// <remarks>
    /// SQLite is the default because a working deployment should need no Postgres and no
    /// Redis. Adding a <em>required</em> external dependency needs an RFC under project
    /// governance; optional backends for shops that want them are welcome.
    /// </remarks>
    public string ConnectionString { get; init; } = "Data Source=gatehouse.db";

    /// <summary>
    /// Whether to create and migrate the schema at startup. Air-gapped and
    /// least-privilege deployments turn this off and apply migrations out of band.
    /// </summary>
    public bool AutoMigrate { get; init; } = true;
}

/// <summary>Observability configuration.</summary>
public sealed class TelemetryOptions
{
    /// <summary>The <c>service.name</c> reported to OpenTelemetry.</summary>
    public string ServiceName { get; init; } = "gatehouse";

    /// <summary>An OTLP endpoint. When null, no OTLP exporter is registered.</summary>
    public string? OtlpEndpoint { get; init; }
}

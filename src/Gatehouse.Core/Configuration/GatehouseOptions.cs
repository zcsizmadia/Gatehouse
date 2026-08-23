
namespace Gatehouse.Configuration;

/// <summary>
/// The root of the Gatehouse configuration surface.
/// </summary>
/// <remarks>
/// <para>
/// Configuration is a first-class product surface rather than an implementation detail.
/// The Phase 2 admin UI is required to have <em>config-as-code parity</em>: anything the UI
/// can change must be expressible in a file that lives in source control. That constraint
/// starts here, so the file format is designed to be reviewed in a pull request.
/// </para>
/// <para>
/// Every property on this type and its children uses <c>set</c> rather than <c>init</c>,
/// which is deliberate and load-bearing. The configuration binding source generator — the
/// AOT-safe replacement for reflection-based binding — emits plain assignments, so it cannot
/// populate an init-only property. It does not fail either: the object binds with every value
/// left at its default. That produces a gateway which starts, reports healthy, and rejects
/// every request because no provider has a <c>Kind</c>. Do not "tidy" these into <c>init</c>.
/// </para>
/// </remarks>
public sealed class GatehouseOptions
{
    /// <summary>The configuration section Gatehouse binds from.</summary>
    public const string SectionName = "Gatehouse";

    /// <summary>
    /// Upstream providers, keyed by the name routes refer to them by.
    /// </summary>
    public IDictionary<string, ProviderOptions> Providers { get; set; } =
        new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Model routes, keyed by the alias callers put in the <c>model</c> request field.
    /// </summary>
    public IDictionary<string, ModelRouteOptions> Models { get; set; } =
        new Dictionary<string, ModelRouteOptions>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where request records are persisted.</summary>
    public StoreOptions Store { get; set; } = new();

    /// <summary>Observability configuration.</summary>
    public TelemetryOptions Telemetry { get; set; } = new();
}

/// <summary>Configuration for one upstream provider.</summary>
public sealed class ProviderOptions
{
    /// <summary>
    /// The provider implementation to use, matching <c>IChatProvider.Name</c> — for example
    /// <c>openai-compatible</c>.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The upstream base address, including any path prefix.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// A literal API key. Provided for local development only.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="ApiKeyEnvironmentVariable"/>, or managed identity where the
    /// provider supports it. A key committed to a configuration file is a key in your git
    /// history, and Gatehouse logs a warning at startup when this property is used.
    /// </remarks>
    public string? ApiKey { get; set; }

    /// <summary>
    /// The name of an environment variable holding the API key. Takes precedence over
    /// <see cref="ApiKey"/> when both are set.
    /// </summary>
    public string? ApiKeyEnvironmentVariable { get; set; }

    /// <summary>
    /// How long to wait for the upstream to respond.
    /// </summary>
    /// <remarks>
    /// This bounds the whole non-streamed call. For streamed calls it bounds the wait for
    /// response headers only: a long generation is not a stalled one, and applying a total
    /// timeout to a stream would cut off exactly the slow, expensive completions that
    /// callers most want to finish.
    /// </remarks>
    public int TimeoutSeconds { get; set; } = 100;

    /// <summary>Extra headers sent on every upstream request.</summary>
    public IDictionary<string, string> Headers { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The API version to request. Azure OpenAI only; ignored by other providers.
    /// </summary>
    /// <remarks>
    /// Left unset, the Azure provider pins a known version rather than tracking the newest.
    /// Azure API versions change response shapes, and a gateway that silently follows the
    /// latest turns an Azure-side rollout into an unexplained Gatehouse regression.
    /// </remarks>
    public string? ApiVersion { get; set; }

    /// <summary>
    /// Authenticate to Azure with a Microsoft Entra managed identity instead of an API key.
    /// </summary>
    /// <remarks>
    /// The recommended setting for anything running in Azure, and the only option here that
    /// stores no credential at all — there is no key to rotate, leak, or find in a backup.
    /// When this is set, <see cref="ApiKey"/> and <see cref="ApiKeyEnvironmentVariable"/> are
    /// ignored.
    /// </remarks>
    public bool UseManagedIdentity { get; set; }

    /// <summary>
    /// The client ID of a user-assigned managed identity, when the host has more than one.
    /// </summary>
    /// <remarks>
    /// Leave unset for a system-assigned identity. On a host with several user-assigned
    /// identities, omitting it produces an authentication failure whose message does not
    /// mention that the identity was ambiguous.
    /// </remarks>
    public string? ManagedIdentityClientId { get; set; }

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
    public bool AllowPassthrough { get; set; }
}

/// <summary>Configuration for one model alias.</summary>
public sealed class ModelRouteOptions
{
    /// <summary>The key in <see cref="GatehouseOptions.Providers"/> that serves this alias.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// The model identifier to send upstream. Defaults to the alias itself, which is the
    /// common case when the caller already names a real model.
    /// </summary>
    public string? UpstreamModel { get; set; }

    /// <summary>
    /// Aliases to try in order if this route fails retryably. Fallback execution lands in
    /// Phase 1; the field is defined here so the configuration format does not have to
    /// change underneath early adopters.
    /// </summary>
    public IList<string> Fallbacks { get; set; } = [];
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
    public string ConnectionString { get; set; } = "Data Source=gatehouse.db";

    /// <summary>
    /// Whether to create and migrate the schema at startup. Air-gapped and
    /// least-privilege deployments turn this off and apply migrations out of band.
    /// </summary>
    public bool AutoMigrate { get; set; } = true;
}

/// <summary>Observability configuration.</summary>
public sealed class TelemetryOptions
{
    /// <summary>The <c>service.name</c> reported to OpenTelemetry.</summary>
    public string ServiceName { get; set; } = "gatehouse";

    /// <summary>An OTLP endpoint. When null, no OTLP exporter is registered.</summary>
    public string? OtlpEndpoint { get; set; }
}

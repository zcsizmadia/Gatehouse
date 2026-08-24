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

    /// <summary>How callers authenticate to the gateway.</summary>
    public AuthenticationOptions Authentication { get; set; } = new();

    /// <summary>How Gatehouse behaves when an upstream misbehaves.</summary>
    public ResilienceOptions Resilience { get; set; } = new();

    /// <summary>Exact-match response caching.</summary>
    public CacheOptions Cache { get; set; } = new();

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
    /// Aliases to try, in order, if this route fails retryably.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolved <em>non-recursively</em>: this list is the whole chain. If <c>a</c> falls back
    /// to <c>b</c> and <c>b</c> falls back to <c>c</c>, a request for <c>a</c> tries <c>a</c>
    /// then <c>b</c> and stops. Transitive chains read as though they compose and then produce
    /// fallback paths nobody declared and no reviewer can see by reading one entry — and they
    /// make cycle detection a prerequisite for correctness rather than a non-issue. Spell the
    /// chain out.
    /// </para>
    /// <para>
    /// Only failures the upstream is responsible for fall through; see
    /// <c>ProviderException.IsRetryable</c>. A malformed request fails on the primary route
    /// and stops, because trying it again elsewhere bills a second provider to produce the
    /// same rejection.
    /// </para>
    /// </remarks>
    public IList<string> Fallbacks { get; set; } = [];
}

/// <summary>How callers prove who they are.</summary>
public enum AuthenticationMode
{
    /// <summary>
    /// Every request must present a valid virtual key. The default.
    /// </summary>
    Required = 0,

    /// <summary>
    /// Requests are accepted without a credential.
    /// </summary>
    /// <remarks>
    /// For local development, and for deployments that authenticate at a layer in front of the
    /// gateway. Gatehouse logs a warning on every startup in this mode: an unauthenticated
    /// gateway holding provider credentials is worth being reminded about, and a warning that
    /// only appeared once would be a warning nobody saw.
    /// </remarks>
    Disabled = 1,
}

/// <summary>Authentication configuration.</summary>
public sealed class AuthenticationOptions
{
    /// <summary>
    /// Whether a virtual key is required. Defaults to <see cref="AuthenticationMode.Required"/>.
    /// </summary>
    /// <remarks>
    /// Secure by default, and it costs something: a fresh deployment with no keys yet will
    /// refuse to start rather than start and reject everything. That is deliberate — see the
    /// startup validation — because a gateway that accepts connections and 401s every request
    /// looks healthy to an orchestrator and gets rolled out everywhere before anyone notices.
    /// </remarks>
    public AuthenticationMode Mode { get; set; } = AuthenticationMode.Required;
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

/// <summary>How Gatehouse behaves when an upstream misbehaves.</summary>
/// <remarks>
/// The defaults are chosen so that turning resilience on changes nothing for a healthy
/// deployment. A breaker that opens under normal operation is worse than no breaker: it
/// converts provider latency into gateway outages and teaches operators to switch the
/// feature off.
/// </remarks>
public sealed class ResilienceOptions
{
    /// <summary>
    /// Whether a retryable failure falls through to the route's configured fallbacks.
    /// </summary>
    /// <remarks>
    /// On by default, but inert unless a route actually declares <c>Fallbacks</c>. Turning it
    /// off is the way to make an incident reproducible: with fallbacks live, the same request
    /// can succeed against a different provider and the primary's failure is visible only in
    /// telemetry.
    /// </remarks>
    public bool FallbacksEnabled { get; set; } = true;

    /// <summary>Whether failing upstreams are taken out of rotation.</summary>
    public bool CircuitBreakerEnabled { get; set; } = true;

    /// <summary>
    /// The fraction of calls in the window that must fail before the circuit opens.
    /// </summary>
    /// <remarks>
    /// Half. Below that a provider is degraded rather than down, and a gateway that stops
    /// using a provider serving half its traffic has made the outage worse than it was.
    /// </remarks>
    public double FailureRatio { get; set; } = 0.5;

    /// <summary>
    /// The number of calls the window must hold before <see cref="FailureRatio"/> is
    /// consulted at all.
    /// </summary>
    /// <remarks>
    /// Without this a single failure on a quiet gateway is a 100% failure rate. Ten is low
    /// enough to react inside one window under real traffic and high enough that a
    /// development deployment sending occasional requests never trips.
    /// </remarks>
    public int MinimumThroughput { get; set; } = 10;

    /// <summary>How much recent history the failure ratio is computed over.</summary>
    public int SamplingWindowSeconds { get; set; } = 30;

    /// <summary>
    /// How long an open circuit rejects calls before admitting one probe.
    /// </summary>
    /// <remarks>
    /// Fifteen seconds is short enough that a provider blip does not become a minute of
    /// gateway downtime, and long enough that a provider being rate-limited gets a pause
    /// rather than a probe on every request.
    /// </remarks>
    public int BreakDurationSeconds { get; set; } = 15;

    /// <summary>
    /// The most upstream calls one client request may cause, including the first.
    /// </summary>
    /// <remarks>
    /// A safety rail rather than a tuning knob. Fallbacks are resolved non-recursively, so a
    /// chain cannot loop, but a route with a long fallback list against a provider outage
    /// would otherwise multiply one client request into as many billed upstream calls as
    /// there are links. Four is two spare providers and a stop.
    /// </remarks>
    public int MaxAttempts { get; set; } = 4;
}

/// <summary>Exact-match response caching.</summary>
/// <remarks>
/// <para>
/// Off by default, deliberately. Caching changes observable behaviour: repeated identical
/// requests stop reaching the provider, so they stop being sampled and start returning a
/// fixed answer, and latency for those falls to almost nothing. Every one of those is
/// usually wanted — but a gateway that starts doing it because someone upgraded is a gateway
/// that changed the output of a caller's application without being asked.
/// </para>
/// <para>
/// Note what caching does <em>not</em> do here: it never serves an answer to a question that
/// merely resembles the stored one. See <c>ResponseCache</c> for why semantic caching is a
/// Phase 4 question rather than a quick win.
/// </para>
/// </remarks>
public sealed class CacheOptions
{
    /// <summary>Whether to serve repeated identical requests from memory.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// How long an entry stays servable.
    /// </summary>
    /// <remarks>
    /// An hour. Long enough to absorb the retry storms and duplicated prompts that make
    /// caching worth having, short enough that a model or deployment change behind an alias
    /// stops being papered over within a working session.
    /// </remarks>
    public int TtlSeconds { get; set; } = 3600;

    /// <summary>
    /// The most entries to hold before the least recently used is dropped.
    /// </summary>
    /// <remarks>
    /// With <see cref="MaxResponseBytes"/> this is the memory bound: worst case is roughly
    /// the product of the two. The defaults come to a few hundred megabytes at absolute
    /// worst and a small fraction of that in practice.
    /// </remarks>
    public int MaxEntries { get; set; } = 10_000;

    /// <summary>The largest response worth storing.</summary>
    /// <remarks>
    /// Responses above this are not cached at all rather than cached and immediately evicted,
    /// because one very long completion would otherwise flush a cache full of useful short
    /// ones.
    /// </remarks>
    public int MaxResponseBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// Whether an entry is only servable to the organisation that created it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On by default, which costs hit rate and is still the right default for this product. A
    /// shared cache means one tenant's spend subsidises another's, that the second tenant's
    /// audit trail shows a completion it never paid for, and that response time reveals
    /// whether somebody else has asked a given question before. None of those is catastrophic
    /// and all of them are surprises, and a governance tool should not hand an operator a
    /// surprise it could have avoided by default.
    /// </para>
    /// <para>
    /// Turn it off for a single-tenant deployment, where there is no boundary to cross and the
    /// hit rate is the only thing that matters.
    /// </para>
    /// </remarks>
    public bool ScopeToOrganisation { get; set; } = true;
}

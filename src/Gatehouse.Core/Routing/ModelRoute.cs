namespace Gatehouse.Routing;

/// <summary>
/// The resolved destination for one model alias.
/// </summary>
/// <remarks>
/// Callers ask for a capability (<c>gpt-4o-mini</c>, <c>fast-summariser</c>) and Gatehouse
/// resolves it to a provider and an upstream model name. Keeping that indirection means an
/// operator can move traffic between providers, or between Azure deployments, without any
/// application redeploying.
/// </remarks>
public sealed record ModelRoute
{
    /// <summary>The model name as the caller requested it.</summary>
    public required string Alias { get; init; }

    /// <summary>
    /// The <see cref="Providers.IChatProvider.Name"/> of the provider that serves this
    /// alias.
    /// </summary>
    public required string Provider { get; init; }

    /// <summary>
    /// The model identifier to send upstream. For Azure OpenAI this is the deployment
    /// name, which is frequently not the same string as the model family.
    /// </summary>
    public required string UpstreamModel { get; init; }

    /// <summary>
    /// Aliases to try, in order, if this route fails with a retryable error. Empty means no
    /// fallback: the caller sees the failure. Populated routing chains arrive in Phase 1.
    /// </summary>
    public IReadOnlyList<string> Fallbacks { get; init; } = [];
}

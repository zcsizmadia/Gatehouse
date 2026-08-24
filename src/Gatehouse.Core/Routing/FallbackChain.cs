namespace Gatehouse.Routing;

/// <summary>
/// Expands a route into the ordered list of routes a request may be attempted against.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="IModelRouter"/> because it answers a different question.
/// The router answers "where does this alias point", which every request asks once. This
/// answers "and where else may I try", which only matters on failure. Splitting them keeps
/// the allocation off the success path.
/// </remarks>
public static class FallbackChain
{
    /// <summary>
    /// Resolves the attempt order for a request.
    /// </summary>
    /// <param name="router">The router that resolves fallback aliases.</param>
    /// <param name="primary">The route the caller's model resolved to.</param>
    /// <param name="maxAttempts">The most routes to return, including <paramref name="primary"/>.</param>
    /// <returns>
    /// <paramref name="primary"/> first, then each resolvable fallback alias in the order it
    /// was configured. Never empty.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Unresolvable aliases are skipped rather than throwing. Startup validation already
    /// rejects a fallback naming an unconfigured model, so reaching this with one means the
    /// configuration was reloaded into an inconsistent state mid-flight — and dropping the
    /// bad link to serve the request beats failing a request that the primary route, or a
    /// later link, could have answered.
    /// </para>
    /// <para>
    /// Deduplicated by alias, which also removes a self-reference. Two links to the same
    /// upstream would consume two of the attempt budget to ask the same failing provider the
    /// same question twice.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ModelRoute> Resolve(IModelRouter router, ModelRoute primary, int maxAttempts)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(primary);

        if (maxAttempts <= 1 || primary.Fallbacks.Count == 0)
        {
            return [primary];
        }

        List<ModelRoute> chain = [primary];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase) { primary.Alias };

        foreach (string alias in primary.Fallbacks)
        {
            if (chain.Count >= maxAttempts)
            {
                break;
            }

            if (!seen.Add(alias))
            {
                continue;
            }

            if (router.TryResolve(alias, out ModelRoute? route))
            {
                chain.Add(route);
            }
        }

        return chain;
    }
}

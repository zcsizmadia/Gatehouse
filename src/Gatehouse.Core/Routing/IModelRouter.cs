using System.Diagnostics.CodeAnalysis;

namespace Gatehouse.Routing;

/// <summary>
/// Resolves a caller-supplied model alias to a concrete provider and upstream model.
/// </summary>
public interface IModelRouter
{
    /// <summary>
    /// Attempts to resolve a model alias.
    /// </summary>
    /// <param name="model">The value of the request's <c>model</c> field.</param>
    /// <param name="route">The resolved route, when the alias is configured.</param>
    /// <returns><see langword="true"/> if the alias is configured.</returns>
    /// <remarks>
    /// An unknown alias is a client error, not a server error, and returning false rather
    /// than throwing keeps that distinction cheap on the hot path.
    /// </remarks>
    bool TryResolve(string model, [NotNullWhen(true)] out ModelRoute? route);

    /// <summary>
    /// Every configured alias, for the <c>/v1/models</c> listing.
    /// </summary>
    IReadOnlyCollection<string> Aliases { get; }
}

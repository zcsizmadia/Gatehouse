using Gatehouse.Configuration;
using Yarp.ReverseProxy.Configuration;

namespace Gatehouse.Server.Infrastructure;

/// <summary>
/// Builds the YARP configuration for provider-native passthrough routes.
/// </summary>
/// <remarks>
/// <para>
/// Two proxying strategies coexist in Gatehouse, and the split is deliberate.
/// </para>
/// <para>
/// The OpenAI-compatible endpoint parses the request, routes it, meters it and records it.
/// That is what governance requires and it costs a body parse per request.
/// </para>
/// <para>
/// Passthrough is the other half: YARP forwards the bytes without understanding them. It is
/// the escape hatch for provider features the common wire format cannot express, and it is
/// the mechanism the Phase 3 MCP gateway will build on, where the traffic being proxied is
/// not chat completions at all. Proving both paths coexist in one host — one binary, one
/// port, one telemetry pipeline — is the point of doing it in the Phase 0 spike rather than
/// discovering an incompatibility in month nine.
/// </para>
/// </remarks>
internal static class PassthroughProxy
{
    /// <summary>The path prefix passthrough routes are served under.</summary>
    public const string PathPrefix = "/passthrough";

    /// <summary>
    /// Builds routes and clusters for every provider that opted in.
    /// </summary>
    /// <param name="providers">The configured providers.</param>
    /// <returns>The YARP route and cluster configuration, both empty when nobody opted in.</returns>
    public static (IReadOnlyList<RouteConfig> Routes, IReadOnlyList<ClusterConfig> Clusters) Build(
        IDictionary<string, ProviderOptions> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        List<RouteConfig> routes = [];
        List<ClusterConfig> clusters = [];

        foreach ((string name, ProviderOptions provider) in providers)
        {
            if (!provider.AllowPassthrough)
            {
                continue;
            }

            string clusterId = $"passthrough-{name}";

            routes.Add(new RouteConfig
            {
                RouteId = clusterId,
                ClusterId = clusterId,
                Match = new RouteMatch { Path = $"{PathPrefix}/{name}/{{**remainder}}" },
                Transforms =
                [
                    // Strip the Gatehouse-specific prefix so the upstream sees the path it
                    // expects. Without this the provider gets "/passthrough/openai/v1/..."
                    // and returns a 404 that looks like a provider outage.
                    new Dictionary<string, string> { ["PathRemovePrefix"] = $"{PathPrefix}/{name}" },
                ],
            });

            clusters.Add(new ClusterConfig
            {
                ClusterId = clusterId,
                Destinations = new Dictionary<string, DestinationConfig>(StringComparer.Ordinal)
                {
                    ["primary"] = new DestinationConfig { Address = provider.BaseUrl.TrimEnd('/') + "/" },
                },
            });
        }

        return (routes, clusters);
    }
}

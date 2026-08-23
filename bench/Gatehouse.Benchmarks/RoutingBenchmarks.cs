using BenchmarkDotNet.Attributes;
using Gatehouse.Configuration;
using Gatehouse.Routing;
using Microsoft.Extensions.Options;

namespace Gatehouse.Benchmarks;

/// <summary>
/// The per-request cost of resolving a model alias.
/// </summary>
/// <remarks>
/// Every inference request performs exactly one of these lookups, which is why the router
/// freezes its table at construction. The benchmark exists to keep that claim honest: if a
/// future change makes routing allocate, this is where it shows up, and the
/// <see cref="MemoryDiagnoserAttribute"/> makes the allocation visible rather than merely
/// implied by a slower time.
/// </remarks>
[MemoryDiagnoser]
public class RoutingBenchmarks
{
    private ModelRouter _router = null!;
    private string[] _aliases = [];

    /// <summary>Number of configured model aliases.</summary>
    [Params(3, 50, 500)]
    public int RouteCount { get; set; }

    /// <summary>Builds a router with the requested number of routes.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var options = new GatehouseOptions();
        _aliases = new string[RouteCount];

        for (int i = 0; i < RouteCount; i++)
        {
            string alias = $"model-{i:D4}";
            _aliases[i] = alias;
            options.Models[alias] = new ModelRouteOptions
            {
                Provider = "openai",
                UpstreamModel = $"upstream-{i:D4}",
            };
        }

        _router = new ModelRouter(Options.Create(options));
    }

    /// <summary>Resolves an alias that exists.</summary>
    [Benchmark(Description = "Resolve a configured alias")]
    public bool ResolveHit() => _router.TryResolve(_aliases[RouteCount / 2], out _);

    /// <summary>
    /// Resolves an alias that does not exist.
    /// </summary>
    /// <remarks>
    /// Measured separately because a misconfigured client can send these at full request rate,
    /// and a miss must not cost more than a hit.
    /// </remarks>
    [Benchmark(Description = "Reject an unknown alias")]
    public bool ResolveMiss() => _router.TryResolve("no-such-model", out _);

    /// <summary>
    /// Resolves an alias whose casing differs from the configured one.
    /// </summary>
    /// <remarks>
    /// Case-insensitive comparison is not free, and clients disagree about casing often
    /// enough that this is a realistic hot path rather than an edge case.
    /// </remarks>
    [Benchmark(Description = "Resolve an alias with different casing")]
    public bool ResolveDifferentCasing() =>
        _router.TryResolve(_aliases[RouteCount / 2].ToUpperInvariant(), out _);
}

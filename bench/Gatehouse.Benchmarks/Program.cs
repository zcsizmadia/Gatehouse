using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

namespace Gatehouse.Benchmarks;

/// <summary>
/// The public benchmark harness.
/// </summary>
/// <remarks>
/// One of the project's three standing rules is that every performance claim ships with the
/// harness that reproduces it. This is that harness: anything Gatehouse says about its own
/// speed must be regenerable here, on the reader's hardware, from a single command.
/// <code>
/// dotnet run -c Release --project bench/Gatehouse.Benchmarks -- --filter '*'
/// </code>
/// </remarks>
public static class Program
{
    /// <summary>Runs the requested benchmarks.</summary>
    /// <param name="args">BenchmarkDotNet arguments, for example <c>--filter '*Streaming*'</c>.</param>
    public static void Main(string[] args) =>
        BenchmarkSwitcher
            .FromAssembly(typeof(Program).Assembly)
            .Run(args, DefaultConfig.Instance.WithOptions(ConfigOptions.DisableOptimizationsValidator));
}

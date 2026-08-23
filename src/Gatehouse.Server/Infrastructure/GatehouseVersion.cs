using System.Reflection;

namespace Gatehouse.Server.Infrastructure;

/// <summary>
/// The version this binary reports.
/// </summary>
/// <remarks>
/// Read from <see cref="AssemblyInformationalVersionAttribute"/>, which CI stamps from the
/// git tag. It appears in telemetry resources, in the health payload and in
/// <c>gatehouse --version</c>, so that a support conversation can start from the exact build
/// rather than from "the latest one, I think".
/// </remarks>
internal static class GatehouseVersion
{
    /// <summary>The informational version, including any commit suffix.</summary>
    public static string Informational { get; } =
        typeof(GatehouseVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0-unknown";
}

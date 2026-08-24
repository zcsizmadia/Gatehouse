using Microsoft.Extensions.Logging;

namespace Gatehouse.Resilience;

/// <summary>Source-generated log messages for fallback and circuit breaking.</summary>
internal static partial class ResilienceLogging
{
    /// <remarks>
    /// Information rather than Warning. A fallback working is the feature behaving as
    /// configured, and logging it at Warning trains operators to ignore warnings. The failure
    /// that caused it is the interesting event and it is logged separately, below.
    /// </remarks>
    [LoggerMessage(
        EventId = 7001,
        Level = LogLevel.Warning,
        Message = "Route '{Alias}' failed retryably at provider '{Provider}' and will fall "
                  + "back if another route is configured: {Reason}")]
    public static partial void RouteFailedRetryably(
        this ILogger logger,
        string alias,
        string provider,
        string reason);

    [LoggerMessage(
        EventId = 7002,
        Level = LogLevel.Information,
        Message = "Skipping route '{Alias}': the circuit for upstream '{Upstream}' is open.")]
    public static partial void CircuitOpenSkippingRoute(this ILogger logger, string alias, string upstream);

    /// <remarks>
    /// Error, because startup validation is supposed to make it unreachable. Seeing it means
    /// either a configuration reload landed in an inconsistent state or the validator has a
    /// gap, and both are Gatehouse's problem rather than the operator's.
    /// </remarks>
    [LoggerMessage(
        EventId = 7003,
        Level = LogLevel.Error,
        Message = "Route '{Alias}' names provider '{Provider}', which is not registered. "
                  + "Skipping it. This indicates a configuration validation gap.")]
    public static partial void RouteProviderMissing(this ILogger logger, string alias, string provider);
}

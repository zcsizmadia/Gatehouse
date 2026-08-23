using Microsoft.Extensions.Logging;

namespace Gatehouse.Providers.Anthropic;

/// <summary>Source-generated log messages for the Anthropic provider.</summary>
internal static partial class AnthropicLogging
{
    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Warning,
        Message = "Provider {ProviderName} returned HTTP {StatusCode} (retryable: {IsRetryable}). {Detail}")]
    public static partial void UpstreamCallFailed(
        this ILogger logger,
        string providerName,
        int statusCode,
        bool isRetryable,
        string detail);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Warning,
        Message = "Provider {ProviderName} emitted a stream event that could not be parsed and was dropped: {Reason}")]
    public static partial void UnparseableStreamEvent(this ILogger logger, string providerName, string reason);

    /// <remarks>
    /// Logged at Error, not Warning. A metering inconsistency means the token counts this
    /// request contributes to a chargeback report cannot be trusted, and the usual cause is a
    /// provider changing its usage semantics underneath us — which will affect every
    /// subsequent request too, silently, until someone notices.
    /// </remarks>
    [LoggerMessage(
        EventId = 3003,
        Level = LogLevel.Error,
        Message = "Provider {ProviderName} reported inconsistent token usage; it has been recorded as "
                  + "estimated rather than measured. {Discrepancy}")]
    public static partial void MeteringDiscrepancy(this ILogger logger, string providerName, string discrepancy);
}

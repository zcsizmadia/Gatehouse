using Microsoft.Extensions.Logging;

namespace Gatehouse.Providers.Google;

/// <summary>Source-generated log messages for the Gemini provider.</summary>
internal static partial class GeminiLogging
{
    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Warning,
        Message = "Provider {ProviderName} returned HTTP {StatusCode} (retryable: {IsRetryable}). {Detail}")]
    public static partial void UpstreamCallFailed(
        this ILogger logger,
        string providerName,
        int statusCode,
        bool isRetryable,
        string detail);

    [LoggerMessage(
        EventId = 4002,
        Level = LogLevel.Warning,
        Message = "Provider {ProviderName} emitted a stream chunk that could not be parsed and was dropped: {Reason}")]
    public static partial void UnparseableStreamChunk(this ILogger logger, string providerName, string reason);

    [LoggerMessage(
        EventId = 4003,
        Level = LogLevel.Error,
        Message = "Provider {ProviderName} reported inconsistent token usage; it has been recorded as "
                  + "estimated rather than measured. {Discrepancy}")]
    public static partial void MeteringDiscrepancy(this ILogger logger, string providerName, string discrepancy);
}

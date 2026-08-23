using Microsoft.Extensions.Logging;

namespace Gatehouse.Providers.OpenAI;

/// <summary>
/// Source-generated log messages for the OpenAI-compatible provider.
/// </summary>
/// <remarks>
/// <see cref="LoggerMessageAttribute"/> rather than <c>logger.LogWarning(...)</c>: the
/// generated code avoids boxing and skips formatting entirely when the level is disabled.
/// It also keeps the message templates in one file, which is what makes them reviewable as
/// a group for accidental credential disclosure.
/// </remarks>
internal static partial class ProviderLogging
{
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Provider {ProviderName} returned HTTP {StatusCode} (retryable: {IsRetryable}). {Detail}")]
    public static partial void UpstreamCallFailed(
        this ILogger logger,
        string providerName,
        int statusCode,
        bool isRetryable,
        string detail);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "Provider {ProviderName} emitted a stream chunk that could not be parsed and was dropped: {Reason}")]
    public static partial void UpstreamChunkUnparseable(
        this ILogger logger,
        string providerName,
        string reason);
}

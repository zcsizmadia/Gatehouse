using Microsoft.Extensions.Logging;

namespace Gatehouse.Providers.AzureOpenAI;

/// <summary>Source-generated log messages for the Azure OpenAI provider.</summary>
internal static partial class AzureOpenAiLogging
{
    /// <remarks>
    /// Information, not Debug. Token acquisition is the step operators most often need to
    /// confirm when a managed identity is misconfigured, and its absence from the log is the
    /// clearest signal that the identity was never used.
    /// </remarks>
    [LoggerMessage(
        EventId = 5001,
        Level = LogLevel.Information,
        Message = "Acquired a Microsoft Entra token for Azure OpenAI, valid until {ExpiresOn:O}.")]
    public static partial void EntraTokenAcquired(this ILogger logger, DateTimeOffset expiresOn);
}

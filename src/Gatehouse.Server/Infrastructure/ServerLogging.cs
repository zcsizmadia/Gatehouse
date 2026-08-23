using Microsoft.Extensions.Logging;

namespace Gatehouse.Server.Infrastructure;

/// <summary>Source-generated log messages for the host.</summary>
internal static partial class ServerLogging
{
    /// <remarks>
    /// Warning, not Information. A rejected credential is either a misconfigured client or an
    /// attempt, and both are worth seeing without turning the level up. The reason is recorded
    /// here even though the HTTP response deliberately does not disclose it.
    /// </remarks>
    [LoggerMessage(
        EventId = 6001,
        Level = LogLevel.Warning,
        Message = "Rejected an unauthenticated request to {Path}: {Reason}.")]
    public static partial void AuthenticationRejected(this ILogger logger, string reason, string path);

    [LoggerMessage(
        EventId = 6002,
        Level = LogLevel.Warning,
        Message = "Authentication is DISABLED. This gateway holds provider credentials and is "
                  + "accepting requests without one. Set Gatehouse:Authentication:Mode to "
                  + "Required, and issue a key with 'gatehouse keys create', unless something "
                  + "in front of the gateway is authenticating callers.")]
    public static partial void AuthenticationDisabled(this ILogger logger);

    [LoggerMessage(
        EventId = 6003,
        Level = LogLevel.Information,
        Message = "Authentication is required. {UsableKeyCount} usable virtual key(s) are configured.")]
    public static partial void AuthenticationEnabled(this ILogger logger, int usableKeyCount);
}

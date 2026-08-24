using Microsoft.Extensions.Logging;

namespace Gatehouse.Providers.Bedrock;

/// <summary>Source-generated log messages for the Bedrock provider.</summary>
internal static partial class BedrockLogging
{
    /// <remarks>
    /// Warning, not Debug. An inconsistent usage report means the recorded token counts for
    /// this request cannot be reconciled against the bill, and the reconciliation report will
    /// surface it as an unexplained variance weeks later. Saying so at the moment it happens is
    /// the difference between a five-minute diagnosis and a month-end mystery — and for Bedrock
    /// specifically, it is the signal that the derived cache semantics have stopped matching.
    /// </remarks>
    [LoggerMessage(
        EventId = 8001,
        Level = LogLevel.Warning,
        Message = "Bedrock reported inconsistent token usage for model {Model}: {Discrepancy}. "
                  + "This request's usage is recorded but will not reconcile.")]
    public static partial void UsageInconsistent(this ILogger logger, string model, string discrepancy);
}

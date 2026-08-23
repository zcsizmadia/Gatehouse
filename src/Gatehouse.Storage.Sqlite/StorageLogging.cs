using Microsoft.Extensions.Logging;

namespace Gatehouse.Storage.Sqlite;

/// <summary>Source-generated log messages for the SQLite store.</summary>
internal static partial class StorageLogging
{
    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "Request log schema ready at version {SchemaVersion}.")]
    public static partial void SchemaReady(this ILogger logger, int schemaVersion);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Error,
        Message = "Failed to write {RecordCount} request log record(s). Inference is unaffected, "
                  + "but this leaves a gap in the usage and audit history.")]
    public static partial void RequestLogWriteFailed(this ILogger logger, int recordCount, Exception exception);

    [LoggerMessage(
        EventId = 2004,
        Level = LogLevel.Debug,
        Message = "Committed {RecordCount} request log record(s).")]
    public static partial void RequestLogBatchWritten(this ILogger logger, int recordCount);

    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Error,
        Message = "The request log writer stopped unexpectedly. Usage and audit records will not "
                  + "be persisted until the gateway is restarted.")]
    public static partial void WriterStoppedUnexpectedly(this ILogger logger, Exception exception);
}

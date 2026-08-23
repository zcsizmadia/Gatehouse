using Microsoft.Data.Sqlite;

namespace Gatehouse.Storage.Sqlite;

/// <summary>
/// Creates and migrates the Gatehouse SQLite schema.
/// </summary>
/// <remarks>
/// <para>
/// Migrations are hand-written SQL applied in order and recorded in a
/// <c>schema_version</c> table. There is no ORM and no migration framework, which is a
/// deliberate trade: the schema is small, it has to be auditable by a DBA who has never seen
/// this codebase, and an air-gapped deployment must be able to apply it from a
/// <c>.sql</c> file with no tooling. All three of those get harder, not easier, with a
/// migration framework in the way.
/// </para>
/// <para>
/// Each migration must be idempotent and must never rewrite existing rows. The request log
/// becomes an audit record in Phase 2, and an audit record that a software upgrade can
/// silently modify is not one.
/// </para>
/// </remarks>
public static class SqliteSchema
{
    /// <summary>The schema version this build expects.</summary>
    public const int CurrentVersion = 1;

    private static readonly string[] Migrations =
    [
        // Version 1 — the request log.
        //
        // Timestamps are ISO-8601 UTC text rather than Unix integers: SQLite sorts them
        // correctly as strings, and a human reading the table with the sqlite3 CLI during an
        // incident can see what they say without converting anything.
        """
        CREATE TABLE IF NOT EXISTS request_log (
            id                          TEXT    NOT NULL PRIMARY KEY,
            timestamp_utc               TEXT    NOT NULL,
            requested_model             TEXT    NOT NULL,
            provider                    TEXT        NULL,
            upstream_model              TEXT        NULL,
            streamed                    INTEGER NOT NULL,
            status_code                 INTEGER NOT NULL,
            prompt_tokens               INTEGER NOT NULL DEFAULT 0,
            completion_tokens           INTEGER NOT NULL DEFAULT 0,
            usage_is_provider_reported  INTEGER NOT NULL DEFAULT 0,
            duration_ms                 REAL    NOT NULL,
            time_to_first_chunk_ms      REAL        NULL,
            error_type                  TEXT        NULL
        ) STRICT;

        CREATE INDEX IF NOT EXISTS ix_request_log_timestamp
            ON request_log (timestamp_utc DESC);

        CREATE INDEX IF NOT EXISTS ix_request_log_model
            ON request_log (requested_model, timestamp_utc DESC);
        """,
    ];

    /// <summary>
    /// Applies any migrations the database is missing.
    /// </summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The schema version after migration.</returns>
    public static async Task<int> MigrateAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await ExecuteAsync(
            connection,
            "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL PRIMARY KEY) STRICT;",
            cancellationToken);

        int applied = await GetVersionAsync(connection, cancellationToken);

        for (int version = applied + 1; version <= Migrations.Length; version++)
        {
            // One transaction per migration. A partially applied migration would leave the
            // database in a state no version number describes, which is the failure mode
            // that makes people afraid to upgrade.
            await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = Migrations[version - 1];
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (SqliteCommand stamp = connection.CreateCommand())
            {
                stamp.Transaction = transaction;
                stamp.CommandText = "INSERT INTO schema_version (version) VALUES ($version);";
                stamp.Parameters.AddWithValue("$version", version);
                await stamp.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return await GetVersionAsync(connection, cancellationToken);
    }

    /// <summary>Reads the highest applied schema version, or 0 for a fresh database.</summary>
    public static async Task<int> GetVersionAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";

        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long version ? (int)version : 0;
    }

    /// <summary>
    /// Applies the connection-level pragmas Gatehouse depends on.
    /// </summary>
    /// <remarks>
    /// <c>journal_mode=WAL</c> lets the background writer commit while readers are querying,
    /// which is the whole reason SQLite is viable here rather than merely convenient.
    /// <c>synchronous=NORMAL</c> is the standard companion setting: with WAL it is durable
    /// across process crashes, losing only the last transactions in an OS-level crash, which
    /// is the right trade for a usage log that must not slow down inference.
    /// <c>foreign_keys=ON</c> costs nothing now and prevents a class of bug once Phase 2 adds
    /// the budget and key tables that reference these rows.
    /// </remarks>
    public static async Task ApplyPragmasAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken);
        await ExecuteAsync(connection, "PRAGMA synchronous = NORMAL;", cancellationToken);
        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
        await ExecuteAsync(connection, "PRAGMA busy_timeout = 5000;", cancellationToken);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

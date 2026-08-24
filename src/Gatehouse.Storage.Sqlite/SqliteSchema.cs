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
/// Each migration must be idempotent, and must not rewrite any <em>recorded fact</em> on an
/// existing row. The request log becomes an audit record in Phase 2, and an audit record that
/// a software upgrade can silently modify is not one.
/// </para>
/// <para>
/// There is one narrow exception, exercised once by version 3, and it is written down here
/// rather than left as a surprise in the migration list. A new column that classifies rows may
/// be backfilled to the value that was always semantically true of them — version 3 marks
/// pre-existing passthrough rows unmetered, which they always were. What must never happen is
/// a migration that changes a token count, a timestamp, a status code or an attribution label:
/// those are the observations, and rewriting an observation is falsifying the record. Adding
/// the exception to this list requires the same justification in the migration's own comment.
/// </para>
/// </remarks>
public static class SqliteSchema
{
    /// <summary>The schema version this build expects.</summary>
    public const int CurrentVersion = 3;

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

        // Version 2 — virtual keys, and attribution on the request log.
        //
        // Lookup is by secret_hash, so that is the indexed column and it is unique: two keys
        // hashing the same would make authentication ambiguous.
        //
        // The attribution columns are added to request_log rather than joined at query time.
        // Keys get relabelled, and a chargeback report for a past period must attribute spend
        // to whoever owned it then. ALTER TABLE ADD COLUMN leaves existing rows untouched,
        // which is what an append-only audit record requires.
        """
        CREATE TABLE IF NOT EXISTS virtual_keys (
            id             TEXT NOT NULL PRIMARY KEY,
            name           TEXT NOT NULL,
            secret_hash    TEXT NOT NULL,
            secret_prefix  TEXT NOT NULL,
            organisation   TEXT     NULL,
            team           TEXT     NULL,
            application    TEXT     NULL,
            created_at_utc TEXT NOT NULL,
            expires_at_utc TEXT     NULL,
            revoked_at_utc TEXT     NULL
        ) STRICT;

        CREATE UNIQUE INDEX IF NOT EXISTS ux_virtual_keys_secret_hash
            ON virtual_keys (secret_hash);

        CREATE INDEX IF NOT EXISTS ix_virtual_keys_created
            ON virtual_keys (created_at_utc DESC);

        ALTER TABLE request_log ADD COLUMN virtual_key_id TEXT NULL;
        ALTER TABLE request_log ADD COLUMN organisation   TEXT NULL;
        ALTER TABLE request_log ADD COLUMN team           TEXT NULL;
        ALTER TABLE request_log ADD COLUMN application    TEXT NULL;

        CREATE INDEX IF NOT EXISTS ix_request_log_attribution
            ON request_log (organisation, team, application, timestamp_utc DESC);
        """,

        // Version 3 — the columns invoice reconciliation needs.
        //
        // Cache reads and cache writes are billed at different rates from ordinary input
        // tokens (roughly a tenth and roughly a premium respectively), so a prompt-token
        // total that cannot separate them can detect a disagreement with an invoice but
        // cannot explain it. Providers already report the split; Gatehouse was computing it
        // and discarding it at the storage boundary.
        //
        // `metered` replaces sniffing for a "(passthrough:...)" prefix on requested_model.
        // Unmetered traffic is the largest single category of legitimately unexplained spend,
        // and it deserves a column rather than a naming convention.
        //
        // Existing rows keep the defaults, which are the honest values for them: no cache
        // split was recorded, and every pre-v3 row that was passthrough is indistinguishable
        // from a metered one by anything except that prefix. The backfill below applies it
        // where the prefix is present, and is the only rewrite of existing rows in the
        // schema's history — justified because leaving those rows claiming to be metered
        // would overstate what Gatehouse can account for.
        """
        ALTER TABLE request_log ADD COLUMN prompt_tokens_cached   INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE request_log ADD COLUMN prompt_tokens_cache_write INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE request_log ADD COLUMN metered                INTEGER NOT NULL DEFAULT 1;

        UPDATE request_log
           SET metered = 0
         WHERE requested_model LIKE '(passthrough:%';

        CREATE INDEX IF NOT EXISTS ix_request_log_usage
            ON request_log (provider, upstream_model, timestamp_utc);
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

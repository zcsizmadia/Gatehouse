using System.Globalization;
using Gatehouse.Configuration;
using Gatehouse.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Gatehouse.Storage.Sqlite;

/// <summary>
/// The default virtual key store, backed by SQLite.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the request log, this store reads and writes synchronously with the caller. Key
/// lookups are on the authentication path, so they must be correct before the request proceeds
/// rather than eventually — and they are a single indexed read on a small table.
/// </para>
/// <para>
/// Each operation opens its own connection. Microsoft.Data.Sqlite pools them, so this is cheap,
/// and it avoids sharing a connection with the request-log writer, whose long-lived write
/// connection would otherwise serialise authentication behind a batch commit.
/// </para>
/// </remarks>
public sealed class SqliteVirtualKeyStore : IVirtualKeyStore
{
    private const string SelectColumns =
        "id, name, secret_hash, secret_prefix, organisation, team, application, "
        + "created_at_utc, expires_at_utc, revoked_at_utc";

    private readonly string _connectionString;

    /// <summary>Creates the store.</summary>
    /// <param name="options">The bound Gatehouse configuration.</param>
    public SqliteVirtualKeyStore(IOptions<GatehouseOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = options.Value.Store.ConnectionString;
    }

    /// <inheritdoc />
    public async ValueTask<VirtualKey?> FindBySecretHashAsync(
        string secretHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(secretHash);

        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"SELECT {SelectColumns} FROM virtual_keys WHERE secret_hash = $hash LIMIT 1;";
        command.Parameters.AddWithValue("$hash", secretHash);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    /// <inheritdoc />
    public async ValueTask AddAsync(VirtualKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO virtual_keys (
                id, name, secret_hash, secret_prefix, organisation, team, application,
                created_at_utc, expires_at_utc, revoked_at_utc
            ) VALUES (
                $id, $name, $hash, $prefix, $organisation, $team, $application,
                $createdAt, $expiresAt, $revokedAt
            );
            """;

        command.Parameters.AddWithValue("$id", key.Id);
        command.Parameters.AddWithValue("$name", key.Name);
        command.Parameters.AddWithValue("$hash", key.SecretHash);
        command.Parameters.AddWithValue("$prefix", key.SecretPrefix);
        command.Parameters.AddWithValue("$organisation", (object?)key.Organisation ?? DBNull.Value);
        command.Parameters.AddWithValue("$team", (object?)key.Team ?? DBNull.Value);
        command.Parameters.AddWithValue("$application", (object?)key.Application ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", Format(key.CreatedAt));
        command.Parameters.AddWithValue("$expiresAt", FormatNullable(key.ExpiresAt));
        command.Parameters.AddWithValue("$revokedAt", FormatNullable(key.RevokedAt));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<bool> RevokeAsync(
        string keyId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);

        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();

        // Only the first revocation is recorded. Revoking twice must not move the timestamp,
        // because the audit question is when the key stopped being valid.
        command.CommandText =
            "UPDATE virtual_keys SET revoked_at_utc = $revokedAt "
            + "WHERE id = $id AND revoked_at_utc IS NULL;";

        command.Parameters.AddWithValue("$id", keyId);
        command.Parameters.AddWithValue("$revokedAt", Format(revokedAt));

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<VirtualKey>> ListAsync(
        bool includeRevoked,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = includeRevoked
            ? $"SELECT {SelectColumns} FROM virtual_keys ORDER BY created_at_utc DESC;"
            : $"SELECT {SelectColumns} FROM virtual_keys WHERE revoked_at_utc IS NULL ORDER BY created_at_utc DESC;";

        List<VirtualKey> keys = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            keys.Add(Read(reader));
        }

        return keys;
    }

    /// <inheritdoc />
    public async ValueTask<int> CountUsableAsync(
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            "SELECT COUNT(*) FROM virtual_keys "
            + "WHERE revoked_at_utc IS NULL AND (expires_at_utc IS NULL OR expires_at_utc > $asOf);";

        command.Parameters.AddWithValue("$asOf", Format(asOf));

        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long count ? (int)count : 0;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await SqliteSchema.ApplyPragmasAsync(connection, cancellationToken);
        return connection;
    }

    private static VirtualKey Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.GetString(1),
        SecretHash = reader.GetString(2),
        SecretPrefix = reader.GetString(3),
        Organisation = reader.IsDBNull(4) ? null : reader.GetString(4),
        Team = reader.IsDBNull(5) ? null : reader.GetString(5),
        Application = reader.IsDBNull(6) ? null : reader.GetString(6),
        CreatedAt = Parse(reader.GetString(7)),
        ExpiresAt = reader.IsDBNull(8) ? null : Parse(reader.GetString(8)),
        RevokedAt = reader.IsDBNull(9) ? null : Parse(reader.GetString(9)),
    };

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static object FormatNullable(DateTimeOffset? value) =>
        value is null ? DBNull.Value : Format(value.Value);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

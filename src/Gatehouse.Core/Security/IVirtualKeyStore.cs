namespace Gatehouse.Security;

/// <summary>
/// Stores and looks up virtual keys.
/// </summary>
/// <remarks>
/// Lookup is <em>by hash</em> rather than by identifier, so the store never needs to enumerate
/// candidates and compare them. That keeps authentication a single indexed read on the hot
/// path, and it means a compromised store yields hashes rather than credentials.
/// </remarks>
public interface IVirtualKeyStore
{
    /// <summary>
    /// Finds the key whose secret hashes to <paramref name="secretHash"/>.
    /// </summary>
    /// <returns>The key, or null if no key matches.</returns>
    /// <remarks>
    /// Returns revoked and expired keys as well as live ones. Whether a key may be
    /// <em>used</em> is a separate decision, made by <see cref="VirtualKeyAuthenticator"/>, so
    /// that the reason for a rejection can be reported accurately instead of every failure
    /// looking like an unknown key.
    /// </remarks>
    ValueTask<VirtualKey?> FindBySecretHashAsync(string secretHash, CancellationToken cancellationToken = default);

    /// <summary>Adds a key. The secret itself is never passed in, only its hash.</summary>
    ValueTask AddAsync(VirtualKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a key revoked.
    /// </summary>
    /// <returns>False if no key with that identifier exists.</returns>
    /// <remarks>
    /// Revocation, not deletion: the request log references keys, and an audit trail pointing
    /// at rows somebody removed is not an audit trail.
    /// </remarks>
    ValueTask<bool> RevokeAsync(string keyId, DateTimeOffset revokedAt, CancellationToken cancellationToken = default);

    /// <summary>Lists keys, newest first. Secrets are not included because they are not stored.</summary>
    ValueTask<IReadOnlyList<VirtualKey>> ListAsync(bool includeRevoked, CancellationToken cancellationToken = default);

    /// <summary>How many keys exist that could currently authenticate a request.</summary>
    /// <remarks>
    /// Used by startup validation. A gateway configured to require authentication but holding
    /// no usable key would accept connections and reject every request — the failure mode that
    /// looks healthy to an orchestrator and gets rolled out everywhere.
    /// </remarks>
    ValueTask<int> CountUsableAsync(DateTimeOffset asOf, CancellationToken cancellationToken = default);
}

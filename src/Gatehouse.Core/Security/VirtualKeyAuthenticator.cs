namespace Gatehouse.Security;

/// <summary>
/// Why an authentication attempt was rejected.
/// </summary>
public enum AuthenticationFailure
{
    /// <summary>Not a failure.</summary>
    None = 0,

    /// <summary>No <c>Authorization: Bearer</c> header was present.</summary>
    MissingCredential,

    /// <summary>The credential does not look like a Gatehouse key.</summary>
    MalformedCredential,

    /// <summary>No key matches the presented secret.</summary>
    UnknownKey,

    /// <summary>The key exists but has been revoked.</summary>
    Revoked,

    /// <summary>The key exists but has expired.</summary>
    Expired,
}

/// <summary>The outcome of an authentication attempt.</summary>
/// <param name="Key">The authenticated key, or null on failure.</param>
/// <param name="Failure">Why it failed, or <see cref="AuthenticationFailure.None"/>.</param>
public readonly record struct AuthenticationResult(VirtualKey? Key, AuthenticationFailure Failure)
{
    /// <summary>Whether authentication succeeded.</summary>
    public bool Succeeded => Failure == AuthenticationFailure.None && Key is not null;

    /// <summary>
    /// A message safe to return to the caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately generic for every failure except expiry. Distinguishing "unknown key" from
    /// "revoked key" in the response would tell an attacker holding a stolen key whether it was
    /// ever valid, which is information they should have to get elsewhere. Expiry is different:
    /// it is almost always a legitimate caller whose key aged out, and telling them so saves a
    /// support ticket without revealing anything an attacker could use — a guessed key is not
    /// going to be reported as expired.
    /// </para>
    /// <para>
    /// The precise reason is always logged. It is the response that is vague, not the record.
    /// </para>
    /// </remarks>
    public string ClientMessage => Failure switch
    {
        AuthenticationFailure.MissingCredential =>
            "This gateway requires an API key. Send it as 'Authorization: Bearer gh-sk-...'.",
        AuthenticationFailure.Expired =>
            "The supplied API key has expired.",
        _ => "The supplied API key is not valid.",
    };
}

/// <summary>
/// Validates a presented credential against the key store.
/// </summary>
/// <remarks>
/// Separated from the HTTP layer so the decision logic is testable without a request, and so
/// the same rules apply wherever a credential is checked — the inference endpoint now, the
/// admin API in Phase 2.
/// </remarks>
public sealed class VirtualKeyAuthenticator
{
    private readonly IVirtualKeyStore _store;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates an authenticator.</summary>
    public VirtualKeyAuthenticator(IVirtualKeyStore store, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _store = store;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Authenticates an <c>Authorization</c> header value.
    /// </summary>
    /// <param name="authorizationHeader">The raw header value, or null if absent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask<AuthenticationResult> AuthenticateAsync(
        string? authorizationHeader,
        CancellationToken cancellationToken = default)
    {
        if (!VirtualKeySecret.TryReadBearerToken(authorizationHeader, out string secret))
        {
            return new AuthenticationResult(null, AuthenticationFailure.MissingCredential);
        }

        // Shape-checked before touching the store, so a caller who pasted a provider key by
        // mistake is rejected without a database read.
        if (!VirtualKeySecret.HasExpectedShape(secret))
        {
            return new AuthenticationResult(null, AuthenticationFailure.MalformedCredential);
        }

        string hash = VirtualKeySecret.Hash(secret);
        VirtualKey? key = await _store.FindBySecretHashAsync(hash, cancellationToken);

        if (key is null)
        {
            return new AuthenticationResult(null, AuthenticationFailure.UnknownKey);
        }

        // Revocation is checked before expiry so that a revoked key which also happens to have
        // expired is reported as revoked — the more actionable of the two for an operator
        // reading the audit log.
        if (key.RevokedAt is not null)
        {
            return new AuthenticationResult(null, AuthenticationFailure.Revoked);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (key.ExpiresAt is not null && key.ExpiresAt <= now)
        {
            return new AuthenticationResult(null, AuthenticationFailure.Expired);
        }

        return new AuthenticationResult(key, AuthenticationFailure.None);
    }
}

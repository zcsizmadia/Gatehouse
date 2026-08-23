namespace Gatehouse.Security;

/// <summary>
/// A credential issued by Gatehouse to a calling application.
/// </summary>
/// <remarks>
/// <para>
/// Virtual keys are the reason applications never hold provider credentials. A client presents
/// a Gatehouse key; Gatehouse presents the upstream credential. Revoking a key stops one
/// application without rotating anything at the provider, and without a conversation with
/// whoever else shares that provider account.
/// </para>
/// <para>
/// They are also what makes chargeback possible. The <see cref="Organisation"/>,
/// <see cref="Team"/> and <see cref="Application"/> labels are the hierarchy that Phase 2's
/// budgets enforce against and that a FinOps export aggregates by, which is why they are
/// recorded from the start rather than added once there is history to lose.
/// </para>
/// <para>
/// The secret is not here. Only <see cref="SecretHash"/> is stored, and it is a one-way hash —
/// a stolen database yields no usable credentials, and Gatehouse itself cannot show an operator
/// a key they have lost.
/// </para>
/// </remarks>
public sealed record VirtualKey
{
    /// <summary>A stable, non-secret identifier, safe to log and to show in a UI.</summary>
    public required string Id { get; init; }

    /// <summary>A human-readable name, for example <c>checkout-service-prod</c>.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The hash of the secret. Never the secret itself.
    /// </summary>
    public required string SecretHash { get; init; }

    /// <summary>
    /// The first few characters of the secret, kept so an operator can recognise which key a
    /// log line refers to without the key being recoverable.
    /// </summary>
    public required string SecretPrefix { get; init; }

    /// <summary>The owning organisation, the top of the chargeback hierarchy.</summary>
    public string? Organisation { get; init; }

    /// <summary>The owning team.</summary>
    public string? Team { get; init; }

    /// <summary>The calling application.</summary>
    public string? Application { get; init; }

    /// <summary>When the key was issued.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the key stops working, or null for no expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// When the key was revoked, or null if it is still live.
    /// </summary>
    /// <remarks>
    /// Revocation is recorded rather than deleted. The request log references keys, and an
    /// audit trail that points at rows somebody removed is not an audit trail.
    /// </remarks>
    public DateTimeOffset? RevokedAt { get; init; }

    /// <summary>Whether the key may be used at <paramref name="now"/>.</summary>
    public bool IsUsableAt(DateTimeOffset now) =>
        RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);
}

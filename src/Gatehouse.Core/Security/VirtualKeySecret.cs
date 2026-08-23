using System.Security.Cryptography;
using System.Text;

namespace Gatehouse.Security;

/// <summary>
/// Generates, parses and hashes virtual key secrets.
/// </summary>
/// <remarks>
/// <para>
/// Secrets are <c>gh-sk-</c> followed by 256 bits of cryptographic randomness, base64url
/// encoded. The prefix is deliberate: it makes a leaked key recognisable to secret scanners —
/// including GitHub's — and it makes an accidentally-pasted key obvious in a code review.
/// </para>
/// <para>
/// <strong>Hashing is SHA-256, not bcrypt or Argon2, and that is a considered choice rather
/// than an oversight.</strong> Password hashes are deliberately slow because passwords have
/// perhaps 30 bits of entropy and must survive an offline dictionary attack. These secrets have
/// 256 bits from a CSPRNG; there is no dictionary and no feasible brute force, so the slowness
/// buys nothing. It would cost something real, though: a hash is computed on every single
/// inference request, and a deliberately-expensive one on the hot path is a denial-of-service
/// vector an unauthenticated caller can trigger for free.
/// </para>
/// </remarks>
public static class VirtualKeySecret
{
    /// <summary>The prefix every Gatehouse-issued secret carries.</summary>
    public const string Prefix = "gh-sk-";

    /// <summary>How many characters after the prefix are retained for display.</summary>
    public const int DisplayPrefixLength = 8;

    private const int SecretByteCount = 32;

    /// <summary>A freshly generated secret and the fields needed to store it.</summary>
    /// <param name="Secret">The full secret. Shown to the operator once and never stored.</param>
    /// <param name="Hash">The hash to persist.</param>
    /// <param name="DisplayPrefix">The recognisable leading characters, safe to store and log.</param>
    public readonly record struct GeneratedSecret(string Secret, string Hash, string DisplayPrefix);

    /// <summary>Generates a new secret.</summary>
    public static GeneratedSecret Generate()
    {
        byte[] entropy = RandomNumberGenerator.GetBytes(SecretByteCount);

        // base64url: no padding, and safe in a header, a URL and a shell argument without
        // quoting. Standard base64 would need escaping in at least one of those.
        //
        // Hand-rolled rather than System.Buffers.Text.Base64Url, which is .NET 9+ — this
        // library also targets net8.0.
        string body = Convert.ToBase64String(entropy)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        string secret = Prefix + body;

        return new GeneratedSecret(
            secret,
            Hash(secret),
            body.Length <= DisplayPrefixLength ? body : body[..DisplayPrefixLength]);
    }

    /// <summary>
    /// Hashes a secret for storage or comparison.
    /// </summary>
    /// <remarks>
    /// Hex-encoded lower case, so the stored value is stable across platforms and readable in a
    /// database client during an incident.
    /// </remarks>
    public static string Hash(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));

        // ToHexStringLower is .NET 9+, and this library also targets net8.0.
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Whether a string looks like a Gatehouse secret.
    /// </summary>
    /// <remarks>
    /// A cheap shape check before touching the store, so a caller sending an obviously wrong
    /// credential — a provider key, say — is rejected without a database lookup.
    /// </remarks>
    public static bool HasExpectedShape(string? candidate) =>
        candidate is not null
        && candidate.StartsWith(Prefix, StringComparison.Ordinal)
        && candidate.Length > Prefix.Length + DisplayPrefixLength;

    /// <summary>
    /// Extracts the bearer token from an <c>Authorization</c> header value.
    /// </summary>
    /// <remarks>
    /// Tolerates the scheme in any casing, because HTTP header schemes are case-insensitive and
    /// client libraries disagree about which casing to send.
    /// </remarks>
    public static bool TryReadBearerToken(string? authorizationHeader, out string token)
    {
        token = string.Empty;

        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return false;
        }

        const string bearer = "Bearer ";
        if (!authorizationHeader.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string value = authorizationHeader[bearer.Length..].Trim();
        if (value.Length == 0)
        {
            return false;
        }

        token = value;
        return true;
    }

    /// <summary>
    /// Compares two hashes without leaking timing information.
    /// </summary>
    /// <remarks>
    /// The store looks keys up <em>by</em> hash, so this is belt and braces rather than the
    /// primary defence. It is here so that any future code path comparing hashes directly does
    /// the right thing by default.
    /// </remarks>
    public static bool HashesMatch(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right));
    }
}

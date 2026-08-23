using Gatehouse.Security;
using Microsoft.Extensions.Time.Testing;

namespace Gatehouse.Tests.Security;

/// <summary>Tests for credential validation.</summary>
public class VirtualKeyAuthenticatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Authenticates_a_live_key()
    {
        var store = new InMemoryKeyStore();
        string secret = store.Add("vk_live");

        AuthenticationResult result = await Authenticator(store).AuthenticateAsync($"Bearer {secret}");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Key!.Id).IsEqualTo("vk_live");
    }

    [Test]
    public async Task Rejects_a_missing_credential()
    {
        AuthenticationResult result = await Authenticator(new InMemoryKeyStore()).AuthenticateAsync(null);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Failure).IsEqualTo(AuthenticationFailure.MissingCredential);
    }

    [Test]
    public async Task Rejects_a_credential_that_is_not_a_gatehouse_key_without_touching_the_store()
    {
        // Shape-checked first, so a caller who pasted a provider key by mistake costs no
        // database read. Asserting on the lookup count is what pins that down.
        var store = new InMemoryKeyStore();

        AuthenticationResult result = await Authenticator(store).AuthenticateAsync("Bearer sk-not-ours");

        await Assert.That(result.Failure).IsEqualTo(AuthenticationFailure.MalformedCredential);
        await Assert.That(store.LookupCount).IsEqualTo(0);
    }

    [Test]
    public async Task Rejects_an_unknown_key()
    {
        var store = new InMemoryKeyStore();
        store.Add("vk_other");

        string stranger = VirtualKeySecret.Generate().Secret;

        AuthenticationResult result = await Authenticator(store).AuthenticateAsync($"Bearer {stranger}");

        await Assert.That(result.Failure).IsEqualTo(AuthenticationFailure.UnknownKey);
    }

    [Test]
    public async Task Rejects_a_revoked_key()
    {
        var store = new InMemoryKeyStore();
        string secret = store.Add("vk_revoked", revokedAt: Now.AddHours(-1));

        AuthenticationResult result = await Authenticator(store).AuthenticateAsync($"Bearer {secret}");

        await Assert.That(result.Failure).IsEqualTo(AuthenticationFailure.Revoked);
    }

    [Test]
    public async Task Rejects_an_expired_key()
    {
        var store = new InMemoryKeyStore();
        string secret = store.Add("vk_expired", expiresAt: Now.AddSeconds(-1));

        AuthenticationResult result = await Authenticator(store).AuthenticateAsync($"Bearer {secret}");

        await Assert.That(result.Failure).IsEqualTo(AuthenticationFailure.Expired);
    }

    [Test]
    public async Task Accepts_a_key_that_has_not_expired_yet()
    {
        var store = new InMemoryKeyStore();
        string secret = store.Add("vk_future", expiresAt: Now.AddMinutes(1));

        AuthenticationResult result = await Authenticator(store).AuthenticateAsync($"Bearer {secret}");

        await Assert.That(result.Succeeded).IsTrue();
    }

    [Test]
    public async Task Reports_revocation_ahead_of_expiry_when_both_apply()
    {
        // The more actionable of the two for whoever is reading the audit log: revocation was a
        // decision somebody made, expiry just happened.
        var store = new InMemoryKeyStore();
        string secret = store.Add("vk_both", expiresAt: Now.AddHours(-2), revokedAt: Now.AddHours(-1));

        AuthenticationResult result = await Authenticator(store).AuthenticateAsync($"Bearer {secret}");

        await Assert.That(result.Failure).IsEqualTo(AuthenticationFailure.Revoked);
    }

    // ---------------------------------------------------------------- disclosure

    [Test]
    public async Task Does_not_tell_the_caller_whether_an_unknown_key_was_ever_valid()
    {
        // A stolen-key holder must not learn from the response whether the key was real. The
        // distinction is logged; it is not returned.
        var store = new InMemoryKeyStore();
        string revoked = store.Add("vk_revoked", revokedAt: Now.AddHours(-1));

        VirtualKeyAuthenticator authenticator = Authenticator(store);

        AuthenticationResult unknown = await authenticator.AuthenticateAsync(
            $"Bearer {VirtualKeySecret.Generate().Secret}");
        AuthenticationResult revokedResult = await authenticator.AuthenticateAsync($"Bearer {revoked}");

        await Assert.That(unknown.ClientMessage).IsEqualTo(revokedResult.ClientMessage);
    }

    [Test]
    public async Task Tells_the_caller_when_a_key_has_merely_expired()
    {
        // Expiry is almost always a legitimate caller whose key aged out, and saying so saves a
        // support ticket. A guessed key is not going to come back as expired, so it discloses
        // nothing an attacker can use.
        var store = new InMemoryKeyStore();
        string secret = store.Add("vk_expired", expiresAt: Now.AddSeconds(-1));

        AuthenticationResult result = await Authenticator(store).AuthenticateAsync($"Bearer {secret}");

        await Assert.That(result.ClientMessage).Contains("expired");
    }

    [Test]
    public async Task Tells_a_caller_with_no_credential_what_to_send()
    {
        AuthenticationResult result = await Authenticator(new InMemoryKeyStore()).AuthenticateAsync(null);

        await Assert.That(result.ClientMessage).Contains("Authorization: Bearer");
    }

    private static VirtualKeyAuthenticator Authenticator(IVirtualKeyStore store) =>
        new(store, new FakeTimeProvider(Now));

    /// <summary>A minimal in-memory store that also counts lookups.</summary>
    private sealed class InMemoryKeyStore : IVirtualKeyStore
    {
        private readonly Dictionary<string, VirtualKey> _byHash = new(StringComparer.Ordinal);

        public int LookupCount { get; private set; }

        public string Add(string id, DateTimeOffset? expiresAt = null, DateTimeOffset? revokedAt = null)
        {
            VirtualKeySecret.GeneratedSecret generated = VirtualKeySecret.Generate();

            _byHash[generated.Hash] = new VirtualKey
            {
                Id = id,
                Name = id,
                SecretHash = generated.Hash,
                SecretPrefix = generated.DisplayPrefix,
                CreatedAt = Now.AddDays(-1),
                ExpiresAt = expiresAt,
                RevokedAt = revokedAt,
            };

            return generated.Secret;
        }

        public ValueTask<VirtualKey?> FindBySecretHashAsync(string secretHash, CancellationToken cancellationToken = default)
        {
            LookupCount++;
            return ValueTask.FromResult(_byHash.GetValueOrDefault(secretHash));
        }

        public ValueTask AddAsync(VirtualKey key, CancellationToken cancellationToken = default)
        {
            _byHash[key.SecretHash] = key;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> RevokeAsync(string keyId, DateTimeOffset revokedAt, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);

        public ValueTask<IReadOnlyList<VirtualKey>> ListAsync(bool includeRevoked, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<VirtualKey>>([.. _byHash.Values]);

        public ValueTask<int> CountUsableAsync(DateTimeOffset asOf, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_byHash.Values.Count(k => k.IsUsableAt(asOf)));
    }
}

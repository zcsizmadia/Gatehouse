using Gatehouse.Security;

namespace Gatehouse.Tests.Security;

/// <summary>Tests for virtual key secret generation, parsing and hashing.</summary>
public class VirtualKeySecretTests
{
    [Test]
    public async Task Generated_secrets_carry_the_recognisable_prefix()
    {
        // The prefix is what lets a secret scanner — GitHub's included — recognise a leaked
        // key, and what makes an accidentally pasted one obvious in review.
        VirtualKeySecret.GeneratedSecret generated = VirtualKeySecret.Generate();

        await Assert.That(generated.Secret).StartsWith("gh-sk-");
    }

    [Test]
    public async Task Generated_secrets_are_url_and_header_safe()
    {
        // base64url, so the secret needs no escaping in a header, a URL or a shell argument.
        // Standard base64 would need it in at least one.
        for (int i = 0; i < 50; i++)
        {
            string secret = VirtualKeySecret.Generate().Secret;

            await Assert.That(secret).DoesNotContain("+");
            await Assert.That(secret).DoesNotContain("/");
            await Assert.That(secret).DoesNotContain("=");
        }
    }

    [Test]
    public async Task Generated_secrets_are_unique()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < 500; i++)
        {
            seen.Add(VirtualKeySecret.Generate().Secret);
        }

        await Assert.That(seen.Count).IsEqualTo(500);
    }

    [Test]
    public async Task The_hash_is_not_the_secret()
    {
        // Stating the obvious as a test, because the consequence of getting it wrong is a
        // database that hands out working credentials.
        VirtualKeySecret.GeneratedSecret generated = VirtualKeySecret.Generate();

        await Assert.That(generated.Hash).IsNotEqualTo(generated.Secret);
        await Assert.That(generated.Hash).DoesNotContain(generated.Secret);
    }

    [Test]
    public async Task Hashing_is_deterministic()
    {
        VirtualKeySecret.GeneratedSecret generated = VirtualKeySecret.Generate();

        await Assert.That(VirtualKeySecret.Hash(generated.Secret)).IsEqualTo(generated.Hash);
    }

    [Test]
    public async Task Hashes_are_lower_case_hex_of_a_fixed_length()
    {
        // SHA-256 as 64 hex characters. Fixed width keeps the stored value stable across
        // platforms and readable in a database client during an incident.
        string hash = VirtualKeySecret.Hash("gh-sk-anything");

        await Assert.That(hash.Length).IsEqualTo(64);
        await Assert.That(hash).IsEqualTo(hash.ToLowerInvariant());
    }

    [Test]
    public async Task The_display_prefix_is_short_and_not_the_whole_secret()
    {
        VirtualKeySecret.GeneratedSecret generated = VirtualKeySecret.Generate();

        await Assert.That(generated.DisplayPrefix.Length).IsEqualTo(VirtualKeySecret.DisplayPrefixLength);
        await Assert.That(generated.Secret).Contains(generated.DisplayPrefix);
        await Assert.That(generated.DisplayPrefix.Length).IsLessThan(generated.Secret.Length);
    }

    // ---------------------------------------------------------------- shape check

    [Test]
    public async Task Recognises_a_well_formed_secret()
    {
        await Assert.That(VirtualKeySecret.HasExpectedShape(VirtualKeySecret.Generate().Secret)).IsTrue();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("sk-openai-style-key")]
    [Arguments("Bearer gh-sk-x")]
    [Arguments("gh-sk-")]
    [Arguments("gh-sk-short")]
    public async Task Rejects_things_that_are_not_gatehouse_secrets(string? candidate)
    {
        // The cheap gate before touching the store, so a caller who pasted a provider key is
        // turned away without a database read.
        await Assert.That(VirtualKeySecret.HasExpectedShape(candidate)).IsFalse();
    }

    // ---------------------------------------------------------------- bearer parsing

    [Test]
    public async Task Reads_a_bearer_token()
    {
        bool ok = VirtualKeySecret.TryReadBearerToken("Bearer gh-sk-abc", out string token);

        await Assert.That(ok).IsTrue();
        await Assert.That(token).IsEqualTo("gh-sk-abc");
    }

    [Test]
    [Arguments("bearer gh-sk-abc")]
    [Arguments("BEARER gh-sk-abc")]
    [Arguments("BeArEr gh-sk-abc")]
    public async Task Accepts_the_scheme_in_any_casing(string header)
    {
        // HTTP schemes are case-insensitive and client libraries disagree about which casing to
        // send. Rejecting one casing would fail for a caller doing nothing wrong.
        bool ok = VirtualKeySecret.TryReadBearerToken(header, out string token);

        await Assert.That(ok).IsTrue();
        await Assert.That(token).IsEqualTo("gh-sk-abc");
    }

    [Test]
    public async Task Trims_surrounding_whitespace_from_the_token()
    {
        VirtualKeySecret.TryReadBearerToken("Bearer   gh-sk-abc  ", out string token);

        await Assert.That(token).IsEqualTo("gh-sk-abc");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("gh-sk-abc")]
    [Arguments("Basic dXNlcjpwYXNz")]
    [Arguments("Bearer")]
    [Arguments("Bearer ")]
    public async Task Rejects_header_values_that_are_not_bearer_tokens(string? header)
    {
        await Assert.That(VirtualKeySecret.TryReadBearerToken(header, out _)).IsFalse();
    }

    // ---------------------------------------------------------------- comparison

    [Test]
    public async Task Matching_hashes_compare_equal()
    {
        string hash = VirtualKeySecret.Hash("gh-sk-abc");

        await Assert.That(VirtualKeySecret.HashesMatch(hash, hash)).IsTrue();
    }

    [Test]
    public async Task Different_hashes_do_not_compare_equal()
    {
        await Assert.That(VirtualKeySecret.HashesMatch(
            VirtualKeySecret.Hash("gh-sk-abc"),
            VirtualKeySecret.Hash("gh-sk-def"))).IsFalse();
    }

    [Test]
    public async Task Hashes_of_different_lengths_do_not_compare_equal()
    {
        // FixedTimeEquals returns false on a length mismatch rather than throwing, which is the
        // behaviour a comparison on the authentication path needs.
        await Assert.That(VirtualKeySecret.HashesMatch("abc", "abcd")).IsFalse();
    }
}

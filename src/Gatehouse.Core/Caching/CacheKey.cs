using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Gatehouse.Routing;
using Gatehouse.Wire;

namespace Gatehouse.Caching;

/// <summary>
/// Computes the exact-match cache key for a request.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Anything that can change the upstream response and is not in this key is a
/// correctness bug.</strong> Two different requests that hash the same will serve one
/// caller another caller's answer, silently, and no test that only checks "a cache hit is
/// fast" will notice. So the safety argument is written down rather than assumed.
/// </para>
/// <para>
/// The argument rests on the provider layer building its upstream body field by field from
/// the typed request surface — see <c>OpenAiCompatibleProvider</c>. Fields a client sends
/// that Gatehouse does not model are dropped, not forwarded, so they cannot affect the
/// response and their absence from this key is sound. <em>If that ever changes — if the
/// request type gains <c>JsonExtensionData</c>, or a provider begins forwarding a raw
/// body — this key becomes unsafe and must be changed in the same commit.</em>
/// </para>
/// <para>
/// Two fields are excluded on purpose:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>stream</c>, because the content of the answer does not depend on how it is delivered.
/// A streamed and a buffered request for the same conversation share one entry, which is
/// both correct and roughly doubles the hit rate on a mixed workload.
/// </description></item>
/// <item><description>
/// <c>user</c>, an opaque end-user identifier providers use for abuse monitoring rather
/// than generation. Including it would give every distinct end user their own cache and
/// reduce the hit rate to near zero for exactly the deployments that most need it.
/// </description></item>
/// </list>
/// <para>
/// Written as canonical JSON through <see cref="Utf8JsonWriter"/> rather than by
/// concatenating strings. Concatenation invites the classic collision — the fields
/// <c>("ab", "c")</c> and <c>("a", "bc")</c> producing one key — and JSON's own escaping
/// removes the whole class of problem without a hand-rolled delimiter scheme.
/// </para>
/// <para>
/// That JSON is hashed as it is produced rather than assembled first, so the cost of a key
/// does not scale with the length of the prompt. See <c>HashingBufferWriter</c>.
/// </para>
/// </remarks>
public static class CacheKey
{
    /// <summary>
    /// The version of the key layout, mixed into every key.
    /// </summary>
    /// <remarks>
    /// Bump this whenever what gets hashed changes. Entries written by an older layout then
    /// cannot be read by a newer one, which is what you want: a key layout change means the
    /// old keys were wrong about something, and serving from them is the bug being fixed.
    /// </remarks>
    public const string LayoutVersion = "v1";

    /// <summary>
    /// Computes the key for a request against a resolved route.
    /// </summary>
    /// <param name="request">The client request.</param>
    /// <param name="route">
    /// The route the request will actually be served by. The <em>resolved</em> route, not the
    /// caller's alias: an operator repointing an alias at a different provider must not keep
    /// serving the previous provider's answers, and a fallback must not populate the primary's
    /// cache entry.
    /// </param>
    /// <param name="organisationScope">
    /// The organisation the key belongs to, when the cache is scoped per organisation, or null
    /// for a gateway-wide cache.
    /// </param>
    public static string Compute(
        ChatCompletionRequest request,
        ModelRoute route,
        string? organisationScope)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(route);

        using var buffer = new HashingBufferWriter();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            writer.WriteString("layout", LayoutVersion);
            writer.WriteString("provider", route.Provider);
            writer.WriteString("model", route.UpstreamModel);

            // Written as an explicit null rather than omitted when unscoped, so that a
            // gateway-wide entry can never collide with one belonging to an organisation
            // whose name happened to be absent.
            if (organisationScope is null)
            {
                writer.WriteNull("org");
            }
            else
            {
                writer.WriteString("org", organisationScope);
            }

            writer.WriteStartArray("messages");

            foreach (ChatMessage message in request.Messages)
            {
                writer.WriteStartObject();
                writer.WriteString("role", message.Role);
                WriteStringOrNull(writer, "content", message.Content);
                WriteStringOrNull(writer, "name", message.Name);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            WriteNumberOrNull(writer, "temperature", request.Temperature);
            WriteNumberOrNull(writer, "top_p", request.TopP);
            WriteNumberOrNull(writer, "max_tokens", request.MaxTokens);

            if (request.Stop is null)
            {
                writer.WriteNull("stop");
            }
            else
            {
                writer.WriteStartArray("stop");

                // Order is preserved rather than sorted. Stop sequences are matched in order
                // by some providers, so two differently ordered lists are not necessarily the
                // same request.
                foreach (string stop in request.Stop)
                {
                    writer.WriteStringValue(stop);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        // Lower-case hex via ToHexString rather than a per-byte format loop: the latter
        // allocates a string per byte. ToLowerInvariant because net8.0 has no
        // ToHexStringLower.
        return Convert.ToHexString(buffer.GetHashAndReset()).ToLowerInvariant();
    }

    private static void WriteStringOrNull(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteNumberOrNull(Utf8JsonWriter writer, string name, float? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteNumber(name, value.Value);
        }
    }

    private static void WriteNumberOrNull(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteNumber(name, value.Value);
        }
    }

    /// <summary>
    /// Feeds JSON straight into a hash without ever holding the whole canonical form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The obvious implementation writes the canonical JSON into a buffer and hashes the
    /// result, and the benchmark harness is what argued against it: a 16 KB
    /// retrieval-augmented prompt allocated roughly 50 KB per request to be hashed, on the path
    /// of every request including the ones that miss. Sizing the buffer better did not help
    /// much, because the buffer <em>was</em> the allocation.
    /// </para>
    /// <para>
    /// Hashing incrementally through one pooled block makes the allocation independent of
    /// prompt length. The block is returned to the pool on dispose, and never cleared: it holds
    /// prompt text, but so does every other buffer this request touches, and the pool hands it
    /// only to the next caller inside the same process.
    /// </para>
    /// </remarks>
    private sealed class HashingBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private const int BlockSize = 4096;

        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private byte[] _block = ArrayPool<byte>.Shared.Rent(BlockSize);

        /// <summary>Consumes the bytes just written and folds them into the hash.</summary>
        public void Advance(int count) => _hash.AppendData(_block, 0, count);

        /// <summary>
        /// Hands back the whole block, growing it if the writer wants more than it holds.
        /// </summary>
        /// <remarks>
        /// Always from index zero: <see cref="Advance"/> has already consumed whatever was
        /// there, so nothing needs preserving between calls. That is what keeps this to a
        /// single block rather than a growing chain of them.
        /// </remarks>
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Grow(sizeHint);
            return _block.AsMemory();
        }

        /// <inheritdoc cref="GetMemory" />
        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Grow(sizeHint);
            return _block.AsSpan();
        }

        /// <summary>The hash of everything written so far.</summary>
        public byte[] GetHashAndReset() => _hash.GetHashAndReset();

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(_block);
            _hash.Dispose();
        }

        private void Grow(int sizeHint)
        {
            if (sizeHint <= _block.Length)
            {
                return;
            }

            ArrayPool<byte>.Shared.Return(_block);
            _block = ArrayPool<byte>.Shared.Rent(sizeHint);
        }
    }
}

using Gatehouse.Caching;
using Gatehouse.Routing;
using Gatehouse.Wire;

namespace Gatehouse.Tests.Caching;

/// <summary>
/// Tests for the exact-match cache key.
/// </summary>
/// <remarks>
/// The whole file is about collisions. Two different requests hashing the same means one
/// caller receives another caller's answer, silently — so every field that can change the
/// upstream response gets a test proving it changes the key, and the two fields deliberately
/// excluded get a test proving they do not.
/// </remarks>
public class CacheKeyTests
{
    private static readonly ModelRoute Route = new()
    {
        Alias = "fast",
        Provider = "openai",
        UpstreamModel = "gpt-4o-mini",
    };

    [Test]
    public async Task Is_stable_for_the_same_request()
    {
        string first = Key(Request());
        string second = Key(Request());

        await Assert.That(first).IsEqualTo(second);

        // A SHA-256 in lower-case hex.
        await Assert.That(first.Length).IsEqualTo(64);
    }

    [Test]
    public async Task Changes_with_the_message_text()
    {
        await AssertDiffers(Key(Request(content: "hello")), Key(Request(content: "goodbye")));
    }

    [Test]
    public async Task Changes_with_the_message_role()
    {
        await AssertDiffers(
            Key(Request(messages: [Message(ChatRoles.User, "x")])),
            Key(Request(messages: [Message(ChatRoles.System, "x")])));
    }

    [Test]
    public async Task Changes_with_the_message_order()
    {
        // A conversation is a sequence. The same messages in a different order are a different
        // question and get a different answer.
        await AssertDiffers(
            Key(Request(messages: [Message(ChatRoles.User, "a"), Message(ChatRoles.User, "b")])),
            Key(Request(messages: [Message(ChatRoles.User, "b"), Message(ChatRoles.User, "a")])));
    }

    [Test]
    public async Task Cannot_be_confused_by_moving_a_boundary_between_fields()
    {
        // The classic concatenation collision: ("ab","c") and ("a","bc") hashing the same. This
        // is the reason the key is written as canonical JSON rather than a joined string.
        await AssertDiffers(
            Key(Request(messages: [Message("ab", "c")])),
            Key(Request(messages: [Message("a", "bc")])));
    }

    [Test]
    public async Task Distinguishes_an_empty_string_from_a_missing_value()
    {
        // Both write something to the key — "" and null — rather than one of them writing
        // nothing at all, which is how a null and an empty field come to collide.
        await AssertDiffers(
            Key(Request(messages: [Message(ChatRoles.User, string.Empty)])),
            Key(Request(messages: [Message(ChatRoles.User, null)])));
    }

    [Test]
    public async Task Changes_with_the_author_name()
    {
        await AssertDiffers(
            Key(Request(messages: [Message(ChatRoles.User, "x")])),
            Key(Request(messages: [Message(ChatRoles.User, "x", name: "alice")])));
    }

    [Test]
    public async Task Changes_with_temperature()
    {
        await AssertDiffers(Key(Request(temperature: 0f)), Key(Request(temperature: 1f)));
    }

    [Test]
    public async Task Distinguishes_an_unset_temperature_from_a_set_one()
    {
        // Unset means "use the provider's default", which is not the same request as asking for
        // a specific value even when that value matches today's default.
        await AssertDiffers(Key(Request()), Key(Request(temperature: 1f)));
    }

    [Test]
    public async Task Changes_with_top_p()
    {
        await AssertDiffers(Key(Request(topP: 0.1f)), Key(Request(topP: 0.9f)));
    }

    [Test]
    public async Task Changes_with_max_tokens()
    {
        await AssertDiffers(Key(Request(maxTokens: 10)), Key(Request(maxTokens: 100)));
    }

    [Test]
    public async Task Changes_with_the_stop_sequences()
    {
        await AssertDiffers(Key(Request()), Key(Request(stop: ["\n"])));
        await AssertDiffers(Key(Request(stop: ["a"])), Key(Request(stop: ["a", "b"])));
    }

    [Test]
    public async Task Changes_with_the_order_of_the_stop_sequences()
    {
        // Not sorted before hashing: some providers match stop sequences in order, so two
        // orderings are not necessarily the same request.
        await AssertDiffers(Key(Request(stop: ["a", "b"])), Key(Request(stop: ["b", "a"])));
    }

    [Test]
    public async Task Changes_with_the_provider_serving_the_route()
    {
        // A fallback must not populate the primary's entry, and repointing an alias must not
        // keep serving the previous provider's answers.
        await AssertDiffers(
            Key(Request()),
            Key(Request(), route: Route with { Provider = "anthropic" }));
    }

    [Test]
    public async Task Changes_with_the_upstream_model()
    {
        await AssertDiffers(
            Key(Request()),
            Key(Request(), route: Route with { UpstreamModel = "gpt-4o" }));
    }

    [Test]
    public async Task Ignores_the_alias_the_caller_used()
    {
        // The upstream model determines the answer; the alias is a routing label. Two aliases
        // pointing at one deployment should share an entry.
        string viaFast = Key(Request());
        string viaCheap = Key(Request(), route: Route with { Alias = "cheap" });

        await Assert.That(viaFast).IsEqualTo(viaCheap);
    }

    [Test]
    public async Task Changes_with_the_organisation_scope()
    {
        await AssertDiffers(Key(Request(), scope: "acme"), Key(Request(), scope: "globex"));
    }

    [Test]
    public async Task Does_not_let_an_unscoped_entry_collide_with_a_scoped_one()
    {
        // The unscoped case writes an explicit null rather than omitting the field, so a
        // gateway-wide entry cannot be mistaken for one belonging to an organisation.
        await AssertDiffers(Key(Request(), scope: null), Key(Request(), scope: string.Empty));
    }

    [Test]
    public async Task Ignores_whether_the_response_is_streamed()
    {
        // The content of an answer does not depend on how it is delivered. Sharing one entry
        // between streamed and buffered callers is correct, and roughly doubles the hit rate on
        // a mixed workload.
        await Assert.That(Key(Request(stream: true))).IsEqualTo(Key(Request(stream: false)));
    }

    [Test]
    public async Task Ignores_the_end_user_identifier()
    {
        // Providers use it for abuse monitoring, not generation. Including it would give every
        // end user a private cache and drive the hit rate to nothing for exactly the
        // deployments that set it.
        await Assert.That(Key(Request(user: "user-123"))).IsEqualTo(Key(Request()));
    }

    private static async Task AssertDiffers(string first, string second) =>
        await Assert.That(first).IsNotEqualTo(second);

    private static string Key(
        ChatCompletionRequest request,
        ModelRoute? route = null,
        string? scope = null) =>
        CacheKey.Compute(request, route ?? Route, scope);

    private static ChatCompletionRequest Request(
        string content = "hello",
        IReadOnlyList<ChatMessage>? messages = null,
        float? temperature = null,
        float? topP = null,
        int? maxTokens = null,
        IReadOnlyList<string>? stop = null,
        bool stream = false,
        string? user = null) =>
        new()
        {
            Model = "fast",
            Messages = messages ?? [Message(ChatRoles.User, content)],
            Temperature = temperature,
            TopP = topP,
            MaxTokens = maxTokens,
            Stop = stop,
            Stream = stream,
            User = user,
        };

    private static ChatMessage Message(string role, string? content, string? name = null) =>
        new() { Role = role, Content = content, Name = name };
}

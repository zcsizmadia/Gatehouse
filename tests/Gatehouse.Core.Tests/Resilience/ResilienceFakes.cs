using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;
using Gatehouse.Providers;
using Gatehouse.Routing;
using Gatehouse.Wire;

namespace Gatehouse.Tests.Resilience;

/// <summary>A router over a fixed set of routes.</summary>
internal sealed class StubRouter : IModelRouter
{
    private readonly Dictionary<string, ModelRoute> _routes = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Aliases => _routes.Keys;

    public ModelRoute Add(string alias, string provider, params string[] fallbacks)
    {
        var route = new ModelRoute
        {
            Alias = alias,
            Provider = provider,
            UpstreamModel = alias,
            Fallbacks = fallbacks,
        };

        _routes[alias] = route;
        return route;
    }

    public bool TryResolve(string model, [NotNullWhen(true)] out ModelRoute? route) =>
        _routes.TryGetValue(model, out route);
}

/// <summary>
/// A provider whose behaviour is scripted per call, and which records what it was asked.
/// </summary>
/// <remarks>
/// Each <c>Then…</c> call queues the outcome of one more call. Calling the provider more times
/// than the script covers throws rather than repeating the last step, so a test that expects
/// two attempts and gets three fails instead of passing quietly.
/// </remarks>
internal sealed class ScriptedProvider : IChatProvider
{
    private readonly Queue<Step> _script = new();

    public ScriptedProvider(string name) => Name = name;

    public string Name { get; }

    /// <summary>How many times this provider was called.</summary>
    public int Calls { get; private set; }

    /// <summary>How many streamed enumerators ran their cleanup.</summary>
    public int Disposals { get; private set; }

    /// <summary>Queues a successful answer.</summary>
    public ScriptedProvider ThenSucceeds(string text = "ok")
    {
        _script.Enqueue(new Step(Text: text));
        return this;
    }

    /// <summary>Queues a failure the dispatcher is allowed to fall back from.</summary>
    public ScriptedProvider ThenFailsRetryably(string message = "upstream is unwell")
    {
        _script.Enqueue(new Step(FailBefore: new ProviderException(
            Name, message, HttpStatusCode.ServiceUnavailable, isRetryable: true)));
        return this;
    }

    /// <summary>Queues a failure that must stop the chain.</summary>
    public ScriptedProvider ThenFailsTerminally(string message = "your request is malformed")
    {
        _script.Enqueue(new Step(FailBefore: new ProviderException(
            Name, message, HttpStatusCode.BadRequest, isRetryable: false)));
        return this;
    }

    /// <summary>Queues a client disconnect.</summary>
    public ScriptedProvider ThenCancels()
    {
        _script.Enqueue(new Step(Cancel: true));
        return this;
    }

    /// <summary>
    /// Queues a stream that delivers a chunk and then dies, which is the case fallback must
    /// refuse to handle.
    /// </summary>
    public ScriptedProvider ThenFailsMidStream()
    {
        _script.Enqueue(new Step(
            Text: "partial",
            FailAfter: new ProviderException(
                Name, "the stream died halfway", HttpStatusCode.BadGateway, isRetryable: true)));
        return this;
    }

    public Task<ChatCompletionResponse> CompleteAsync(
        ChatCompletionRequest request,
        ModelRoute route,
        CancellationToken cancellationToken)
    {
        Step step = Next();

        if (step.Cancel)
        {
            throw new OperationCanceledException();
        }

        if (step.FailBefore is { } failure)
        {
            throw failure;
        }

        return Task.FromResult(new ChatCompletionResponse
        {
            Id = "chatcmpl-test",
            Created = 0,
            Model = route.UpstreamModel,
            Choices =
            [
                new ChatChoice
                {
                    Index = 0,
                    Message = new ChatMessage { Role = ChatRoles.Assistant, Content = step.Text },
                    FinishReason = "stop",
                },
            ],
            Usage = TokenUsage.FromProvider(1, 1),
        });
    }

    public async IAsyncEnumerable<ChatCompletionChunk> StreamAsync(
        ChatCompletionRequest request,
        ModelRoute route,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Step step = Next();

        try
        {
            // Thrown from inside the enumerator, which is where a real provider's failure
            // surfaces: at the first MoveNextAsync, not at the call that handed back the
            // enumerable. A fake that throws early would let a dispatcher bug through.
            if (step.Cancel)
            {
                throw new OperationCanceledException();
            }

            if (step.FailBefore is { } before)
            {
                throw before;
            }

            if (step.Text is not null)
            {
                yield return Chunk(step.Text, route.UpstreamModel);
                await Task.Yield();
            }

            if (step.FailAfter is { } after)
            {
                throw after;
            }
        }
        finally
        {
            Disposals++;
        }
    }

    private Step Next()
    {
        Calls++;

        if (_script.Count == 0)
        {
            throw new InvalidOperationException(
                $"Provider '{Name}' was called {Calls} time(s) but the script only covers "
                + $"{Calls - 1}. The test exercised more than it described.");
        }

        return _script.Dequeue();
    }

    private static ChatCompletionChunk Chunk(string text, string model) => new()
    {
        Id = "chatcmpl-test",
        Created = 0,
        Model = model,
        Choices =
        [
            new ChatChunkChoice
            {
                Index = 0,
                Delta = new ChatDelta { Content = text },
            },
        ],
    };

    private sealed record Step(
        string? Text = null,
        ProviderException? FailBefore = null,
        ProviderException? FailAfter = null,
        bool Cancel = false);
}

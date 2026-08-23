using Gatehouse.Routing;

namespace Gatehouse.Providers.OpenAI;

/// <summary>
/// Decides where on an upstream a chat completion request is sent.
/// </summary>
/// <remarks>
/// <para>
/// The OpenAI chat completions <em>payload</em> is a de facto standard: OpenAI, Azure OpenAI,
/// Ollama, vLLM and Foundry Local all accept it unchanged. The <em>addressing</em> is not.
/// Azure puts the deployment name in the path and requires an <c>api-version</c> query
/// parameter; everyone else posts to a fixed relative path and names the model in the body.
/// </para>
/// <para>
/// Separating the two means one carefully-tested request builder, SSE reader and usage parser
/// serves every OpenAI-compatible upstream, with the differences confined to a few lines of
/// URL construction. The alternative — a second near-identical provider — is how two
/// implementations drift until only one of them has the streaming fix.
/// </para>
/// </remarks>
public interface IUpstreamAddressing
{
    /// <summary>
    /// Builds the request URI, relative to the configured base address, for a chat completion.
    /// </summary>
    /// <param name="route">The resolved route, whose upstream model name may form part of the path.</param>
    string BuildChatCompletionsUri(ModelRoute route);
}

/// <summary>
/// Standard OpenAI addressing: a fixed path, with the model named in the request body.
/// </summary>
/// <remarks>
/// Correct for OpenAI itself and for every OpenAI-compatible server — Ollama, vLLM and
/// Foundry Local included.
/// </remarks>
public sealed class OpenAiAddressing : IUpstreamAddressing
{
    /// <summary>A shared instance; the type is stateless.</summary>
    public static OpenAiAddressing Instance { get; } = new();

    /// <inheritdoc />
    public string BuildChatCompletionsUri(ModelRoute route) => "chat/completions";
}

/// <summary>
/// Azure OpenAI addressing: the deployment name in the path, plus an API version.
/// </summary>
/// <remarks>
/// <para>
/// Azure identifies the target by <em>deployment</em>, which is an operator-chosen name that
/// frequently differs from the model family. That is exactly what
/// <see cref="ModelRoute.UpstreamModel"/> holds, so an operator writes the deployment name
/// there and callers keep using whatever alias they already use.
/// </para>
/// <para>
/// The deployment name is URL-escaped. Azure permits characters in deployment names that are
/// not safe in a path segment, and an unescaped name produces a 404 that looks like a
/// misconfigured deployment rather than a gateway bug.
/// </para>
/// </remarks>
public sealed class AzureOpenAiAddressing : IUpstreamAddressing
{
    /// <summary>
    /// The API version used when configuration does not specify one.
    /// </summary>
    /// <remarks>
    /// Pinned rather than tracking "latest". Azure API versions change response shapes, and a
    /// gateway that silently follows the newest one turns an Azure-side rollout into an
    /// unexplained Gatehouse regression.
    /// </remarks>
    public const string DefaultApiVersion = "2024-10-21";

    private readonly string _apiVersion;

    /// <summary>Creates Azure addressing.</summary>
    /// <param name="apiVersion">The <c>api-version</c> to request, or null for the default.</param>
    public AzureOpenAiAddressing(string? apiVersion = null) =>
        _apiVersion = string.IsNullOrWhiteSpace(apiVersion) ? DefaultApiVersion : apiVersion;

    /// <inheritdoc />
    public string BuildChatCompletionsUri(ModelRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);

        string deployment = Uri.EscapeDataString(route.UpstreamModel);
        return $"openai/deployments/{deployment}/chat/completions?api-version={_apiVersion}";
    }
}

using System.Net.Http.Headers;
using Azure.Core;
using Microsoft.Extensions.Logging;

namespace Gatehouse.Providers.AzureOpenAI;

/// <summary>
/// Attaches a Microsoft Entra bearer token to every outgoing Azure OpenAI request.
/// </summary>
/// <remarks>
/// <para>
/// Implemented as a <see cref="DelegatingHandler"/> rather than inside the provider so that
/// authentication is orthogonal to the wire format. Azure OpenAI speaks the OpenAI-compatible
/// payload, so the existing provider serves it unchanged; only addressing and credentials
/// differ, and both belong on the <see cref="HttpClient"/> rather than in the request builder.
/// </para>
/// <para>
/// This is the reason managed identity is worth the dependency: there is no stored credential
/// at all. Nothing to rotate, nothing to leak into a config file or a container image, and
/// nothing for an auditor to ask where it lives.
/// </para>
/// </remarks>
public sealed class EntraTokenHandler : DelegatingHandler
{
    /// <summary>
    /// The scope Azure OpenAI data-plane calls are issued against.
    /// </summary>
    public const string CognitiveServicesScope = "https://cognitiveservices.azure.com/.default";

    /// <summary>
    /// How far before expiry a cached token is treated as stale.
    /// </summary>
    /// <remarks>
    /// Renewing early avoids the case where a token passes the check, then expires while the
    /// request is in flight — which for a long streamed completion is a real window, and
    /// surfaces as a mid-generation 401 rather than as an obvious auth failure.
    /// </remarks>
    private static readonly TimeSpan RenewalMargin = TimeSpan.FromMinutes(5);

    private readonly TokenCredential _credential;
    private readonly string[] _scopes;
    private readonly ILogger<EntraTokenHandler> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _renewalLock = new(1, 1);

    private AccessToken _cachedToken;

    /// <summary>Creates the handler.</summary>
    /// <param name="credential">The credential to acquire tokens from.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">Clock, so expiry handling is testable.</param>
    /// <param name="scope">The scope to request; defaults to the Cognitive Services data plane.</param>
    public EntraTokenHandler(
        TokenCredential credential,
        ILogger<EntraTokenHandler> logger,
        TimeProvider timeProvider,
        string? scope = null)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _credential = credential;
        _logger = logger;
        _timeProvider = timeProvider;
        _scopes = [string.IsNullOrWhiteSpace(scope) ? CognitiveServicesScope : scope];
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string token = await GetTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await base.SendAsync(request, cancellationToken);
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (IsUsable(_cachedToken))
        {
            return _cachedToken.Token;
        }

        await _renewalLock.WaitAsync(cancellationToken);
        try
        {
            // Re-checked inside the lock: under load many requests reach the renewal path at
            // once, and without this every one of them would make its own token request.
            if (IsUsable(_cachedToken))
            {
                return _cachedToken.Token;
            }

            AccessToken token = await _credential.GetTokenAsync(
                new TokenRequestContext(_scopes),
                cancellationToken);

            _cachedToken = token;
            _logger.EntraTokenAcquired(token.ExpiresOn);

            return token.Token;
        }
        finally
        {
            _renewalLock.Release();
        }
    }

    private bool IsUsable(AccessToken token) =>
        !string.IsNullOrEmpty(token.Token)
        && token.ExpiresOn - RenewalMargin > _timeProvider.GetUtcNow();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _renewalLock.Dispose();
        }

        base.Dispose(disposing);
    }
}

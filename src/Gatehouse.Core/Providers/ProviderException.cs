using System.Net;
using Gatehouse.Wire;

namespace Gatehouse.Providers;

/// <summary>
/// Thrown when an upstream provider call fails.
/// </summary>
/// <remarks>
/// The distinction that matters here is <see cref="IsRetryable"/>. A gateway that retries a
/// non-retryable failure turns one rejected request into several billed ones, and a gateway
/// that fails to retry a transient one hands the caller an outage the provider did not
/// have. Both mistakes are expensive, so the classification is explicit at the throw site
/// rather than inferred later from a status code.
/// </remarks>
public sealed class ProviderException : Exception
{
    /// <summary>Creates a provider exception.</summary>
    /// <param name="providerName">The provider that failed.</param>
    /// <param name="message">A message safe to return to the caller.</param>
    /// <param name="statusCode">The upstream status code, when there was one.</param>
    /// <param name="isRetryable">Whether a fallback route may be tried.</param>
    /// <param name="innerException">The underlying failure.</param>
    public ProviderException(
        string providerName,
        string message,
        HttpStatusCode? statusCode = null,
        bool isRetryable = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ProviderName = providerName;
        StatusCode = statusCode;
        IsRetryable = isRetryable;
    }

    /// <summary>The provider that failed.</summary>
    public string ProviderName { get; }

    /// <summary>The upstream HTTP status code, when the failure carried one.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// Whether the request may be retried against a fallback route. False for anything the
    /// caller must fix — a malformed request, an unknown model, a rejected credential.
    /// </summary>
    public bool IsRetryable { get; }

    /// <summary>
    /// Classifies an upstream status code the way Gatehouse retries on it.
    /// </summary>
    /// <remarks>
    /// 408, 429 and 5xx are transient. 402 is not: a provider account that is out of credit
    /// will still be out of credit on the retry, and hammering it produces a support ticket
    /// rather than a completion. 401 and 403 are configuration errors that retrying hides.
    /// </remarks>
    public static bool IsRetryableStatus(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.RequestTimeout => true,
        HttpStatusCode.TooManyRequests => true,
        HttpStatusCode.PaymentRequired => false,
        HttpStatusCode.Unauthorized => false,
        HttpStatusCode.Forbidden => false,
        _ => (int)statusCode >= 500,
    };

    /// <summary>
    /// Creates the client-facing error envelope for this failure.
    /// </summary>
    /// <remarks>
    /// <see cref="Exception.Message"/> is used verbatim, which is why every throw site is
    /// responsible for keeping upstream credentials, internal hostnames and stack traces out
    /// of it. The provider name is safe to disclose and materially shortens the debugging
    /// loop for whoever is on the other end.
    /// </remarks>
    public ErrorResponse ToErrorResponse() =>
        ErrorResponse.Create(Message, ErrorTypes.Upstream, ProviderName);
}

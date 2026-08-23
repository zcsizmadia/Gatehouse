using System.Text.Json.Serialization;

namespace Gatehouse.Wire;

/// <summary>
/// An OpenAI-compatible error envelope.
/// </summary>
/// <remarks>
/// Errors keep the same shape as OpenAI returns so that client libraries surface them
/// through their normal exception types instead of failing to parse the body and reporting
/// something unhelpful. A gateway that returns its own error format makes every upstream
/// failure look like a gateway bug.
/// </remarks>
public sealed class ErrorResponse
{
    /// <summary>The error detail.</summary>
    [JsonPropertyName("error")]
    public required ErrorDetail Error { get; init; }

    /// <summary>Creates an error envelope.</summary>
    /// <param name="message">A message safe to show the caller.</param>
    /// <param name="type">The OpenAI-compatible error type.</param>
    /// <param name="code">A machine-readable code, when one applies.</param>
    public static ErrorResponse Create(string message, string type, string? code = null) =>
        new() { Error = new ErrorDetail { Message = message, Type = type, Code = code } };
}

/// <summary>The body of an <see cref="ErrorResponse"/>.</summary>
public sealed class ErrorDetail
{
    /// <summary>
    /// A human-readable message. This crosses a trust boundary to the caller, so it must
    /// never carry upstream credentials, internal hostnames, or stack traces.
    /// </summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>The error class, for example <c>invalid_request_error</c>.</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>The offending request field, when the error is attributable to one.</summary>
    [JsonPropertyName("param")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Param { get; init; }

    /// <summary>A machine-readable error code.</summary>
    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; init; }
}

/// <summary>The error type values Gatehouse emits.</summary>
public static class ErrorTypes
{
    /// <summary>The request was malformed or referenced something that does not exist.</summary>
    public const string InvalidRequest = "invalid_request_error";

    /// <summary>The caller is not authenticated, or the virtual key is unknown or revoked.</summary>
    public const string Authentication = "authentication_error";

    /// <summary>The caller is authenticated but not permitted to do this.</summary>
    public const string Permission = "permission_error";

    /// <summary>A rate limit or a budget ceiling was reached.</summary>
    public const string RateLimit = "rate_limit_error";

    /// <summary>The upstream provider failed in a way Gatehouse could not recover from.</summary>
    public const string Upstream = "upstream_error";

    /// <summary>Gatehouse itself failed.</summary>
    public const string Internal = "internal_error";
}

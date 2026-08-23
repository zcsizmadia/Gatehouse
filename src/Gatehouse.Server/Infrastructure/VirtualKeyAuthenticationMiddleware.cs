using System.Text.Json;
using Gatehouse.Configuration;
using Gatehouse.Diagnostics;
using Gatehouse.Security;
using Gatehouse.Wire;
using Microsoft.Extensions.Options;

namespace Gatehouse.Server.Infrastructure;

/// <summary>
/// Rejects inference requests that do not present a valid virtual key.
/// </summary>
/// <remarks>
/// <para>
/// Runs only on the inference and passthrough surfaces. Health endpoints stay open because an
/// orchestrator probing them has no credential and should not need one — a liveness probe that
/// requires a secret is a liveness probe that fails during credential rotation.
/// </para>
/// <para>
/// The authenticated key is placed in <see cref="HttpContext.Items"/> rather than on a
/// <c>ClaimsPrincipal</c>. Phase 2 introduces OIDC and real principals for the admin surface;
/// until then a claims identity would imply an authorisation model that does not exist yet, and
/// implying one is how a gateway ends up trusted for decisions it never made.
/// </para>
/// </remarks>
public sealed class VirtualKeyAuthenticationMiddleware
{
    /// <summary>The <see cref="HttpContext.Items"/> key holding the authenticated virtual key.</summary>
    public const string AuthenticatedKeyItem = "gatehouse.virtual-key";

    private readonly RequestDelegate _next;
    private readonly VirtualKeyAuthenticator _authenticator;
    private readonly AuthenticationMode _mode;
    private readonly ILogger<VirtualKeyAuthenticationMiddleware> _logger;

    /// <summary>Creates the middleware.</summary>
    public VirtualKeyAuthenticationMiddleware(
        RequestDelegate next,
        VirtualKeyAuthenticator authenticator,
        IOptions<GatehouseOptions> options,
        ILogger<VirtualKeyAuthenticationMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _authenticator = authenticator;
        _mode = options.Value.Authentication.Mode;
        _logger = logger;
    }

    /// <summary>Handles one request.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_mode == AuthenticationMode.Disabled || !RequiresAuthentication(context.Request.Path))
        {
            await _next(context);
            return;
        }

        AuthenticationResult result = await _authenticator.AuthenticateAsync(
            context.Request.Headers.Authorization.ToString(),
            context.RequestAborted);

        if (!result.Succeeded)
        {
            // The precise reason is logged; the response stays vague. Telling a caller whether a
            // key was ever valid hands an attacker holding a stolen key information they would
            // otherwise have to get elsewhere.
            _logger.AuthenticationRejected(result.Failure.ToString(), context.Request.Path);

            GatehouseTelemetry.RequestsRejected.Add(
                1,
                new KeyValuePair<string, object?>(
                    GatehouseTelemetry.Attributes.ErrorType,
                    $"auth_{result.Failure.ToString().ToLowerInvariant()}"));

            await WriteUnauthorizedAsync(context, result);
            return;
        }

        context.Items[AuthenticatedKeyItem] = result.Key;

        await _next(context);
    }

    /// <summary>
    /// Whether a path is part of the credentialed surface.
    /// </summary>
    /// <remarks>
    /// An allow-list of protected prefixes rather than a deny-list of open ones. A new endpoint
    /// added later is then unauthenticated only if somebody says so explicitly, which is the
    /// safer direction for the mistake to fall.
    /// </remarks>
    private static bool RequiresAuthentication(PathString path) =>
        path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments(PassthroughProxy.PathPrefix, StringComparison.OrdinalIgnoreCase);

    private static async Task WriteUnauthorizedAsync(HttpContext context, AuthenticationResult result)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json; charset=utf-8";

        // The scheme, so a compliant client knows what to send next.
        context.Response.Headers.WWWAuthenticate = "Bearer realm=\"gatehouse\"";

        ErrorResponse error = ErrorResponse.Create(
            result.ClientMessage,
            ErrorTypes.Authentication,
            "invalid_api_key");

        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            error,
            GatehouseJsonContext.Default.ErrorResponse,
            CancellationToken.None);
    }
}

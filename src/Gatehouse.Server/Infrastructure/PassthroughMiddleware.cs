using System.Collections.Frozen;
using System.Diagnostics;
using Gatehouse.Storage;
using Microsoft.Extensions.Primitives;

namespace Gatehouse.Server.Infrastructure;

/// <summary>
/// Swaps the caller's virtual key for the upstream credential on passthrough routes, and
/// records the request as unmetered.
/// </summary>
/// <remarks>
/// <para>
/// The credential swap happens in middleware rather than in a YARP header transform so the
/// secret is never written into route configuration, where it would surface in any config
/// dump or diagnostics endpoint that reflects over it.
/// </para>
/// <para>
/// The record is the more important half. A passthrough request cannot be priced, because
/// Gatehouse never parses the body. Writing nothing would make those requests invisible;
/// writing zero tokens without qualification would make them look free. Recording them with
/// <c>UsageIsProviderReported = false</c> makes the gap explicit, so a chargeback report can
/// say "this much traffic bypassed metering" rather than quietly under-reporting.
/// </para>
/// </remarks>
internal sealed class PassthroughMiddleware
{
    private readonly RequestDelegate _next;
    private readonly FrozenDictionary<string, string?> _credentials;
    private readonly IRequestLogStore _store;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the middleware.</summary>
    /// <param name="next">The next component in the pipeline.</param>
    /// <param name="credentials">Resolved upstream credentials, keyed by provider name.</param>
    /// <param name="store">The request log.</param>
    /// <param name="timeProvider">Clock.</param>
    public PassthroughMiddleware(
        RequestDelegate next,
        FrozenDictionary<string, string?> credentials,
        IRequestLogStore store,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _next = next;
        _credentials = credentials;
        _store = store;
        _timeProvider = timeProvider;
    }

    /// <summary>Handles one request.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!TryGetProviderName(context.Request.Path, out string? providerName)
            || !_credentials.TryGetValue(providerName, out string? apiKey))
        {
            await _next(context);
            return;
        }

        // Overwrite rather than append. The caller presented a Gatehouse virtual key; letting
        // any part of it reach the provider would turn the gateway into a credential relay.
        context.Request.Headers.Authorization = string.IsNullOrEmpty(apiKey)
            ? StringValues.Empty
            : $"Bearer {apiKey}";

        long start = Stopwatch.GetTimestamp();
        DateTimeOffset startedAt = _timeProvider.GetUtcNow();

        try
        {
            await _next(context);
        }
        finally
        {
            await _store.RecordAsync(
                new RequestRecord
                {
                    Id = $"passthrough-{Guid.NewGuid():N}",
                    Timestamp = startedAt,
                    RequestedModel = $"(passthrough:{providerName})",
                    Provider = providerName,
                    UpstreamModel = null,
                    Streamed = false,
                    StatusCode = context.Response.StatusCode,
                    UsageIsProviderReported = false,
                    Duration = Stopwatch.GetElapsedTime(start),
                    ErrorType = context.Response.StatusCode >= 400 ? "passthrough_upstream_error" : null,
                },
                CancellationToken.None);
        }
    }

    /// <summary>
    /// Extracts the provider segment from <c>/passthrough/{provider}/...</c>.
    /// </summary>
    private static bool TryGetProviderName(PathString path, out string providerName)
    {
        providerName = string.Empty;

        if (!path.StartsWithSegments(PassthroughProxy.PathPrefix, out PathString remainder)
            || !remainder.HasValue)
        {
            return false;
        }

        ReadOnlySpan<char> rest = remainder.Value.AsSpan(1);
        int slash = rest.IndexOf('/');
        ReadOnlySpan<char> segment = slash < 0 ? rest : rest[..slash];

        if (segment.IsEmpty)
        {
            return false;
        }

        providerName = segment.ToString();
        return true;
    }
}

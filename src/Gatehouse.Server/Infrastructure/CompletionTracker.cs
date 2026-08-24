using System.Diagnostics;
using Gatehouse.Diagnostics;
using Gatehouse.Routing;
using Gatehouse.Security;
using Gatehouse.Storage;
using Gatehouse.Wire;

namespace Gatehouse.Server.Infrastructure;

/// <summary>
/// Measures one inference request and, when it ends, emits its telemetry and writes its
/// record.
/// </summary>
/// <remarks>
/// <para>
/// Every request needs a span, three metrics and a persisted row, and every one of those has
/// to be emitted on the failure path as well as the success path. Spreading that across the
/// endpoint produces the usual outcome: the happy path is instrumented, the error paths are
/// instrumented inconsistently, and the requests operators most want to see are the ones
/// missing from the dashboard. Collecting it here means there is exactly one place where a
/// request can end, and it cannot end without being recorded.
/// </para>
/// <para>
/// Timings come from <see cref="Stopwatch.GetTimestamp"/> rather than from wall-clock
/// subtraction, so a clock adjustment during a long generation cannot produce a negative
/// duration in the billing data.
/// </para>
/// </remarks>
internal sealed class CompletionTracker
{
    private readonly IRequestLogStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly long _startTimestamp;
    private readonly DateTimeOffset _startedAt;
    private readonly Activity? _activity;
    private readonly ChatCompletionRequest _request;

    private long? _firstChunkTimestamp;

    private CompletionTracker(
        ChatCompletionRequest request,
        IRequestLogStore store,
        TimeProvider timeProvider,
        Activity? activity)
    {
        _request = request;
        _store = store;
        _timeProvider = timeProvider;
        _activity = activity;
        _startTimestamp = Stopwatch.GetTimestamp();
        _startedAt = timeProvider.GetUtcNow();

        Id = $"chatcmpl-{Guid.NewGuid():N}";
    }

    /// <summary>The completion identifier, generated up front so it can be logged even on failure.</summary>
    public string Id { get; }

    /// <summary>The resolved route, once routing has succeeded.</summary>
    public ModelRoute? Route { get; set; }

    /// <summary>
    /// The virtual key that authorised the request, when authentication is enabled.
    /// </summary>
    /// <remarks>
    /// Its labels are copied onto the record rather than referenced, so a chargeback report for
    /// a past period attributes spend to whoever owned it then — keys get relabelled as
    /// applications move between teams.
    /// </remarks>
    public VirtualKey? AuthenticatedKey { get; set; }

    /// <summary>Whether the response came from the cache rather than a provider.</summary>
    /// <remarks>
    /// Recorded on the row so that the usage aggregation can keep these tokens out of the
    /// billed totals. They are real tokens and nobody was charged for them.
    /// </remarks>
    public bool ServedFromCache { get; set; }

    /// <summary>
    /// Begins tracking a request and opens its span.
    /// </summary>
    public static CompletionTracker Start(
        ChatCompletionRequest request,
        IRequestLogStore store,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);

        // The GenAI conventions name the span "<operation> <model>", which is what makes
        // traces group usefully by model in a backend that knows nothing about Gatehouse.
        Activity? activity = GatehouseTelemetry.ActivitySource.StartActivity(
            $"{GatehouseTelemetry.Operations.Chat} {request.Model}",
            ActivityKind.Client);

        if (activity is not null)
        {
            activity.SetTag(GatehouseTelemetry.Attributes.OperationName, GatehouseTelemetry.Operations.Chat);
            activity.SetTag(GatehouseTelemetry.Attributes.RequestModel, request.Model);
            activity.SetTag(GatehouseTelemetry.Attributes.GatehouseStreamed, request.Stream);

            if (request.Temperature is { } temperature)
            {
                activity.SetTag(GatehouseTelemetry.Attributes.RequestTemperature, temperature);
            }

            if (request.TopP is { } topP)
            {
                activity.SetTag(GatehouseTelemetry.Attributes.RequestTopP, topP);
            }

            if (request.MaxTokens is { } maxTokens)
            {
                activity.SetTag(GatehouseTelemetry.Attributes.RequestMaxTokens, maxTokens);
            }
        }

        return new CompletionTracker(request, store, timeProvider, activity);
    }

    /// <summary>
    /// Records that the first streamed chunk has been flushed to the client.
    /// </summary>
    /// <remarks>
    /// Called at the flush, not at the point the chunk arrives from the provider. The
    /// difference between those two moments is exactly the latency a buffering bug adds, and
    /// measuring the earlier one would report the bug as fixed.
    /// </remarks>
    public void MarkFirstChunkFlushed() => _firstChunkTimestamp ??= Stopwatch.GetTimestamp();

    /// <summary>The Unix-seconds creation time to stamp on responses.</summary>
    public long CreatedUnixSeconds => _startedAt.ToUnixTimeSeconds();

    /// <summary>
    /// Ends the request: closes the span, emits metrics, and queues the record.
    /// </summary>
    /// <param name="statusCode">The status returned to the caller.</param>
    /// <param name="usage">Token counts, when the request got far enough to have any.</param>
    /// <param name="responseModel">The model that answered.</param>
    /// <param name="finishReason">Why generation stopped.</param>
    /// <param name="errorType">The error class, or null on success.</param>
    /// <param name="cancellationToken">
    /// Application shutdown, not the client's token: the client may well have disconnected,
    /// and that is precisely when the record still needs writing.
    /// </param>
    public async Task CompleteAsync(
        int statusCode,
        TokenUsage? usage,
        string? responseModel = null,
        string? finishReason = null,
        string? errorType = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan duration = Stopwatch.GetElapsedTime(_startTimestamp);
        TimeSpan? timeToFirstChunk = _firstChunkTimestamp is { } first
            ? Stopwatch.GetElapsedTime(_startTimestamp, first)
            : null;

        string provider = Route?.Provider ?? "unrouted";

        if (_activity is not null)
        {
            _activity.SetTag(GatehouseTelemetry.Attributes.GatehouseProvider, provider);
            _activity.SetTag(GatehouseTelemetry.Attributes.GatehouseRouteAlias, _request.Model);

            if (Route is not null)
            {
                _activity.SetTag(GatehouseTelemetry.Attributes.ResponseModel, responseModel ?? Route.UpstreamModel);
            }

            if (finishReason is not null)
            {
                _activity.SetTag(GatehouseTelemetry.Attributes.ResponseFinishReasons, finishReason);
            }

            if (usage is not null)
            {
                _activity.SetTag(GatehouseTelemetry.Attributes.UsageInputTokens, usage.PromptTokens);
                _activity.SetTag(GatehouseTelemetry.Attributes.UsageOutputTokens, usage.CompletionTokens);
            }

            if (errorType is not null)
            {
                _activity.SetTag(GatehouseTelemetry.Attributes.ErrorType, errorType);
                _activity.SetStatus(ActivityStatusCode.Error, errorType);
            }

            _activity.Dispose();
        }

        var baseTags = new TagList
        {
            { GatehouseTelemetry.Attributes.OperationName, GatehouseTelemetry.Operations.Chat },
            { GatehouseTelemetry.Attributes.RequestModel, _request.Model },
            { GatehouseTelemetry.Attributes.GatehouseProvider, provider },
        };

        if (errorType is not null)
        {
            var errorTags = baseTags;
            errorTags.Add(GatehouseTelemetry.Attributes.ErrorType, errorType);
            GatehouseTelemetry.OperationDuration.Record(duration.TotalSeconds, errorTags);
        }
        else
        {
            GatehouseTelemetry.OperationDuration.Record(duration.TotalSeconds, baseTags);
        }

        if (timeToFirstChunk is { } ttfc)
        {
            GatehouseTelemetry.TimeToFirstChunk.Record(ttfc.TotalSeconds, baseTags);
        }

        if (usage is not null)
        {
            var inputTags = baseTags;
            inputTags.Add(GatehouseTelemetry.Attributes.TokenKind, GatehouseTelemetry.TokenKinds.Input);
            GatehouseTelemetry.TokenUsage.Record(usage.PromptTokens, inputTags);

            var outputTags = baseTags;
            outputTags.Add(GatehouseTelemetry.Attributes.TokenKind, GatehouseTelemetry.TokenKinds.Output);
            GatehouseTelemetry.TokenUsage.Record(usage.CompletionTokens, outputTags);
        }

        await _store.RecordAsync(
            new RequestRecord
            {
                Id = Id,
                Timestamp = _startedAt,
                RequestedModel = _request.Model,
                Provider = Route?.Provider,
                UpstreamModel = responseModel ?? Route?.UpstreamModel,
                Streamed = _request.Stream,
                StatusCode = statusCode,
                PromptTokens = usage?.PromptTokens ?? 0,
                CompletionTokens = usage?.CompletionTokens ?? 0,

                // Persisted separately because the provider bills them separately: a cache
                // read at a fraction of the input rate, a cache write at a premium. Folding
                // them into the prompt total makes a variance against an invoice detectable
                // but not explainable.
                CachedPromptTokens = usage?.CachedPromptTokens ?? 0,
                CacheCreationTokens = usage?.CacheCreationTokens ?? 0,

                // Distinct from the two above, and easily confused with them. Those are the
                // *provider's* prompt cache, and the tokens were billed at a discount. This is
                // Gatehouse's own response cache, and the tokens were not billed at all — so
                // the usage aggregation excludes this row from the billed totals entirely.
                ServedFromCache = ServedFromCache,

                // Absent usage is recorded as not-provider-reported rather than defaulting to
                // true. A zero that claims to be authoritative is worse than a zero that
                // admits it is unknown, because only the second one can be reconciled later.
                UsageIsProviderReported = usage?.IsProviderReported ?? false,
                Duration = duration,
                TimeToFirstChunk = timeToFirstChunk,
                ErrorType = errorType,

                // Labels copied, not referenced. See AuthenticatedKey.
                VirtualKeyId = AuthenticatedKey?.Id,
                Organisation = AuthenticatedKey?.Organisation,
                Team = AuthenticatedKey?.Team,
                Application = AuthenticatedKey?.Application,
            },
            cancellationToken);
    }

    /// <summary>The clock, for callers that need response timestamps from the same source.</summary>
    public TimeProvider TimeProvider => _timeProvider;
}

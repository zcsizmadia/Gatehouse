namespace Gatehouse.Streaming;

/// <summary>
/// One dispatched server-sent event.
/// </summary>
/// <param name="Data">
/// The event payload. Multiple <c>data:</c> lines in one event are joined with a single
/// newline, as the SSE specification requires.
/// </param>
/// <param name="EventType">
/// The value of the <c>event:</c> field, or <see langword="null"/> when the event did not
/// carry one. OpenAI-compatible completion streams do not use it; some providers do.
/// </param>
/// <param name="Id">The value of the <c>id:</c> field, when present.</param>
public readonly record struct ServerSentEvent(string Data, string? EventType = null, string? Id = null)
{
    /// <summary>
    /// The sentinel payload that terminates an OpenAI-compatible completion stream.
    /// </summary>
    public const string DoneSentinel = "[DONE]";

    /// <summary>
    /// Whether this event is the end-of-stream sentinel rather than a content chunk.
    /// </summary>
    public bool IsDone => string.Equals(Data, DoneSentinel, StringComparison.Ordinal);
}

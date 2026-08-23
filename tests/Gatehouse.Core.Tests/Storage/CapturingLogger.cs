using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Gatehouse.Tests.Storage;

/// <summary>
/// An <see cref="ILogger{T}"/> that records what was logged.
/// </summary>
/// <remarks>
/// The SQLite store deliberately swallows write failures — losing the request log must not
/// take the gateway down — and reports them through the logger instead. A test using
/// <c>NullLogger</c> therefore sees a store that returns no rows and no reason, which turns
/// a one-line diagnosis into an afternoon. Capturing the log lets a test assert on the cause
/// rather than on the symptom.
/// </remarks>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<string> _entries = new();

    private readonly ConcurrentQueue<string> _all = new();

    /// <summary>Everything logged at <see cref="LogLevel.Error"/> or above.</summary>
    public IReadOnlyCollection<string> Failures => [.. _entries];

    /// <summary>Everything logged, at any level, in order.</summary>
    public IReadOnlyCollection<string> AllEntries => [.. _all];

    /// <summary>Every log entry as one string, for diagnosing a failing assertion.</summary>
    public string Trace => string.Join(Environment.NewLine, AllEntries);

    /// <summary>A single string describing every recorded failure, for assertion messages.</summary>
    public string FailureSummary => Failures.Count == 0
        ? "(no failures logged)"
        : string.Join(Environment.NewLine, Failures);

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc />
    /// <remarks>
    /// Enabled for every level. Source-generated log methods check this before formatting, so
    /// returning false for the lower levels would make the trace silently incomplete — which
    /// is the opposite of what a diagnostic logger is for.
    /// </remarks>
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        string message = formatter(state, exception);
        string entry = exception is null
            ? $"[{logLevel}] {message}"
            : $"[{logLevel}] {message} :: {exception}";

        _all.Enqueue(entry);

        if (logLevel >= LogLevel.Error)
        {
            _entries.Enqueue(entry);
        }
    }
}

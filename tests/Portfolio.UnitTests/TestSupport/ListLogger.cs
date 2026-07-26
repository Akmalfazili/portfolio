using Microsoft.Extensions.Logging;

namespace Portfolio.UnitTests.TestSupport;

/// <summary>A minimal <see cref="ILogger{TCategoryName}"/> that records every formatted log line,
/// so a test can assert on exactly what would have reached a real log sink — used to prove
/// <c>RedactingLoggingHandler</c> never emits a live API key.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Messages.Add(formatter(state, exception));
    }
}

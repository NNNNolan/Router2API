using Microsoft.Extensions.Logging;
using Router.Infrastructure.Persistence;

namespace Router.Tests;

internal sealed class SqlCaptureLogger : ILogger<SqlSugarDatabase>
{
    private readonly List<string> _statements = [];
    public IReadOnlyList<string> Statements => _statements;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Debug) _statements.Add(formatter(state, exception));
    }
}

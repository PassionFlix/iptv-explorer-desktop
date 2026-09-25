using Microsoft.Extensions.Logging;

namespace IPTVExplorer.Infrastructure;

public sealed class RedactingFileLoggerProvider(AppPaths paths) : ILoggerProvider
{
    private readonly object _gate = new();
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }

    private void Write(LogLevel level, string category, string message)
    {
        paths.EnsureCreated();
        var line = $"{DateTimeOffset.Now:O} [{level}] {category}: {LogRedactor.Redact(message)}{Environment.NewLine}";
        var path = Path.Combine(paths.Logs, $"desktop-{DateTimeOffset.Now:yyyyMMdd}.log");
        lock (_gate) File.AppendAllText(path, line);
    }

    private sealed class FileLogger(RedactingFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (exception is not null) message += $" | {exception.GetType().Name}: {exception.Message}";
            owner.Write(logLevel, category, message);
        }
    }
}

using IPTVExplorer.Core;
using IPTVExplorer.Infrastructure;
using Microsoft.Extensions.Logging;

namespace IPTVExplorer.Tests;

public sealed class PlaybackDiagnosticTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("1", true)]
    public void StalkerTraceRequiresExactOptIn(string? value, bool expected) =>
        Assert.Equal(expected, SafePlaybackDiagnosticTrace.IsOptInEnabled(value));

    [Fact]
    public void DisabledTraceProducesNoLog()
    {
        var logger = new RecordingLogger<SafePlaybackDiagnosticTrace>();
        var trace = new SafePlaybackDiagnosticTrace(logger, enabled: false);

        trace.Write("STALKER PLAYBACK RESOLVED", new PlaybackDiagnosticField("scheme", "https"));

        Assert.False(trace.Enabled);
        Assert.Empty(logger.Messages);
    }

    [Fact]
    public void EnabledTraceRejectsRawUrlsAndRedactsCredentialPatterns()
    {
        var logger = new RecordingLogger<SafePlaybackDiagnosticTrace>();
        var trace = new SafePlaybackDiagnosticTrace(logger, enabled: true);

        trace.Write("STALKER PLAYBACK RESOLVED",
            new PlaybackDiagnosticField("unsafe", "https://media.example.invalid/live.php?token=private-token"),
            new PlaybackDiagnosticField("credential_pattern", "password=private-password"));

        var message = Assert.Single(logger.Messages);
        Assert.DoesNotContain("media.example.invalid", message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-token", message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-password", message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", message, StringComparison.Ordinal);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}

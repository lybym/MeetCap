namespace MeetCap.Cli.Logging;

using System.Text;
using MeetCap.Core.Secrets;
using Microsoft.Extensions.Logging;

/// <summary>
/// Console logger provider that redacts known secret values before any text is
/// written to the output stream (docs/ARCHITECTURE.md section 22: API credentials
/// must not be written to normal logs).
/// </summary>
public sealed class RedactingConsoleLoggerProvider : ILoggerProvider
{
    private readonly SecretRegistry _secrets;
    private readonly LogLevel _minimumLevel;
    private readonly TextWriter _writer;
    private readonly Dictionary<string, RedactingConsoleLogger> _loggers = new(StringComparer.Ordinal);

    public RedactingConsoleLoggerProvider(SecretRegistry secrets, LogLevel minimumLevel, TextWriter writer)
    {
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _minimumLevel = minimumLevel;
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public ILogger CreateLogger(string categoryName)
    {
        if (!_loggers.TryGetValue(categoryName, out var logger))
        {
            logger = new RedactingConsoleLogger(categoryName, _secrets, _minimumLevel, _writer);
            _loggers[categoryName] = logger;
        }

        return logger;
    }

    public void Dispose()
    {
        _loggers.Clear();
    }
}

/// <summary>
/// Minimal structured logger: emits <c>[LEVEL] category: message</c> (plus an
/// optional exception) to the configured stream, with secret values masked.
/// </summary>
internal sealed class RedactingConsoleLogger : ILogger
{
    private readonly string _category;
    private readonly SecretRegistry _secrets;
    private readonly LogLevel _minimumLevel;
    private readonly TextWriter _writer;

    public RedactingConsoleLogger(string category, SecretRegistry secrets, LogLevel minimumLevel, TextWriter writer)
    {
        _category = category;
        _secrets = secrets;
        _minimumLevel = minimumLevel;
        _writer = writer;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimumLevel && logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string>? formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter is null ? state?.ToString() ?? string.Empty : formatter(state, exception);
        var redacted = SecretRedactor.Redact(message, _secrets.Values);

        var sb = new StringBuilder();
        sb.Append('[').Append(logLevel.ToString().ToUpperInvariant()).Append("] ");
        sb.Append(_category).Append(": ").Append(redacted);
        if (exception is not null)
        {
            sb.Append(" | ").Append(SecretRedactor.Redact(exception.Message, _secrets.Values));
        }

        _writer.WriteLine(sb.ToString());
        _writer.Flush();
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

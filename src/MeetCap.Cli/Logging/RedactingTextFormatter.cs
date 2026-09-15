namespace MeetCap.Cli.Logging;

using System.IO;
using MeetCap.Core.Secrets;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;

/// <summary>
/// Serilog <see cref="ITextFormatter"/> decorator that masks known secret values
/// in the fully rendered event before it is handed to any sink.
/// </summary>
/// <remarks>
/// Redaction is deliberately applied to the finished text rather than to message
/// properties: a secret can reach a log through a message template, an exception
/// message, or a rendered property, and docs/ARCHITECTURE.md section 22 requires
/// that API credentials are never written to normal logs through any of them.
/// Because the masking happens in the formatter, every sink configured with this
/// formatter inherits the guarantee.
/// </remarks>
internal sealed class RedactingTextFormatter : ITextFormatter
{
    private readonly ITextFormatter _inner;
    private readonly SecretRegistry _secrets;

    public RedactingTextFormatter(ITextFormatter inner, SecretRegistry secrets)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
    }

    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        using var buffer = new StringWriter();
        _inner.Format(logEvent, buffer);
        output.Write(SecretRedactor.Redact(buffer.ToString(), _secrets.Values));
    }
}

/// <summary>
/// Minimal Serilog sink that writes already-formatted log text to a
/// <see cref="TextWriter"/>. Used instead of a console sink so the redaction
/// formatter is the single formatting authority and the CLI keeps zero
/// third-party sink dependencies.
/// </summary>
internal sealed class RedactingTextWriterSink : ILogEventSink
{
    private readonly ITextFormatter _formatter;
    private readonly TextWriter _writer;
    private readonly object _gate = new();

    public RedactingTextWriterSink(ITextFormatter formatter, TextWriter writer)
    {
        _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        lock (_gate)
        {
            _formatter.Format(logEvent, _writer);
            _writer.Flush();
        }
    }
}

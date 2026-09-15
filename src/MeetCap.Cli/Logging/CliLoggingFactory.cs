namespace MeetCap.Cli.Logging;

using System.IO;
using MeetCap.Core.Secrets;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

/// <summary>
/// Builds the production logging pipeline. Serilog is the logging framework
/// (docs/ARCHITECTURE.md section 2 and the #9 OSS baseline) and the redaction
/// formatter wraps it, so credential material is masked before any sink writes.
/// </summary>
internal static class CliLoggingFactory
{
    /// <summary>Console-facing output template, mirroring the previous CLI format.</summary>
    internal const string OutputTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Creates a Serilog logger writing redacted structured events to
    /// <paramref name="writer"/>.
    /// </summary>
    public static Serilog.ILogger Create(
        SecretRegistry secrets,
        TextWriter writer,
        LogLevel minimumLevel = LogLevel.Information,
        LoggingLevelSwitch? levelSwitch = null)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(writer);

        var configuration = new LoggerConfiguration()
            .WriteTo.Sink(new RedactingTextWriterSink(CreateFormatter(secrets), writer));

        if (levelSwitch is null)
        {
            configuration.MinimumLevel.Is(MapLevel(minimumLevel));
        }
        else
        {
            configuration.MinimumLevel.ControlledBy(levelSwitch);
        }

        return configuration.CreateLogger();
    }

    /// <summary>
    /// Creates the redaction formatter over the CLI's console output template.
    /// Redaction lives in the formatter so every sink configured with it inherits
    /// the guarantee that credential material never reaches the output.
    /// </summary>
    public static Serilog.Formatting.ITextFormatter CreateFormatter(SecretRegistry secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        return new RedactingTextFormatter(new MessageTemplateTextFormatter(OutputTemplate), secrets);
    }

    private static LogEventLevel MapLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => LogEventLevel.Verbose,
        LogLevel.Debug => LogEventLevel.Debug,
        LogLevel.Information => LogEventLevel.Information,
        LogLevel.Warning => LogEventLevel.Warning,
        LogLevel.Error => LogEventLevel.Error,
        LogLevel.Critical => LogEventLevel.Fatal,
        _ => LogEventLevel.Fatal,
    };
}

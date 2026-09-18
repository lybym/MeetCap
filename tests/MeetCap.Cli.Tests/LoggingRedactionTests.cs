using System.IO;
using MeetCap.Cli.Logging;
using MeetCap.Core.Configuration;
using MeetCap.Core.Secrets;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeetCap.Cli.Tests;

/// <summary>
/// Proves that the production logging pipeline is Serilog-based and that secrets
/// are masked on the way to the sink, covering the redaction discipline required
/// by Issue #2 and docs/ARCHITECTURE.md section 22.
/// </summary>
public class LoggingRedactionTests
{
    private const string Sentinel = "meetcap-sentinel-secret-value";

    private static SecretRegistry RegistryWith(params string[] secrets)
    {
        var registry = new SecretRegistry();
        registry.UpdateFrom(new MeetCapConfiguration
        {
            Asr = new AsrSection
            {
                Volcengine = new VolcengineSection
                {
                    ApiKey = secrets.Length > 0 ? secrets[0] : string.Empty,
                },
            },
        });
        return registry;
    }

    [Fact]
    public void SerilogPipeline_MasksSecretCarriedThroughMessageAndException()
    {
        var secrets = RegistryWith(Sentinel);
        using var writer = new StringWriter();
        var logger = CliLoggingFactory.Create(secrets, writer, LogLevel.Information);

        logger.Information("submitting batch with api_key={ApiKey}", Sentinel);
        logger.Error(new InvalidOperationException($"provider rejected {Sentinel}"), "call failed");

        var text = writer.ToString();
        Assert.DoesNotContain(Sentinel, text);
        Assert.Contains("***", text);
    }

    [Fact]
    public void SerilogPipeline_MasksTheApiKeyInEveryPositionOfAMessage()
    {
        var secrets = RegistryWith(Sentinel);
        using var writer = new StringWriter();
        var logger = CliLoggingFactory.Create(secrets, writer, LogLevel.Information);

        logger.Information("api_key={ApiKey} provider=volcengine", Sentinel);

        Assert.DoesNotContain(Sentinel, writer.ToString());
    }

    [Fact]
    public void MicrosoftExtensionsLoggingBridge_MasksSecretThroughSerilog()
    {
        var secrets = RegistryWith(Sentinel);
        using var writer = new StringWriter();
        using var loggerFactory = new Serilog.Extensions.Logging.SerilogLoggerFactory(
            CliLoggingFactory.Create(secrets, writer, LogLevel.Information));

        // Command handlers log through Microsoft.Extensions.Logging; the production
        // factory bridges those calls into the redacting Serilog pipeline.
        var logger = loggerFactory.CreateLogger("MeetCap");
        logger.LogInformation("api_key loaded: {ApiKey}", Sentinel);

        var text = writer.ToString();
        Assert.DoesNotContain(Sentinel, text);
        Assert.Contains("***", text);
    }

    [Fact]
    public void NoRegisteredSecret_NothingIsMasked_RegistryIsLoadBearing()
    {
        using var writer = new StringWriter();
        var logger = CliLoggingFactory.Create(new SecretRegistry(), writer, LogLevel.Information);

        // With no loaded configuration the registry is empty, so an unknown value is
        // written verbatim. Commands therefore must load configuration before logging.
        logger.Information("api_key={ApiKey}", Sentinel);

        Assert.Contains(Sentinel, writer.ToString());
    }

    [Fact]
    public void ProductionPipeline_RendersConsoleOutputTemplate()
    {
        var secrets = RegistryWith(Sentinel);
        using var writer = new StringWriter();
        var logger = CliLoggingFactory.Create(secrets, writer, LogLevel.Information);

        logger.Information("value={Value}", Sentinel);

        var rendered = writer.ToString();
        Assert.Contains("[", rendered);
        Assert.Contains("INF", rendered);
        Assert.Contains("value=***", rendered);
    }
}

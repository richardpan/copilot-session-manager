using System.Runtime.InteropServices;
using CopilotSessionManager.Core.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace CopilotSessionManager.Logging;

/// <summary>
/// Serilog enricher that attaches static build / environment context to every
/// log event: <c>AppVersion</c>, <c>OS</c>, and <c>CopilotCliVersion</c>. The
/// values are captured once when the enricher is constructed and stamped onto
/// every subsequent event.
/// </summary>
public sealed class BuildInfoEnricher : ILogEventEnricher
{
    private readonly LogEventProperty _appVersion;
    private readonly LogEventProperty _os;
    private readonly LogEventProperty _copilotCliVersion;

    public BuildInfoEnricher(string copilotCliVersion)
    {
        _appVersion = new LogEventProperty("AppVersion", new ScalarValue(AppMetadata.Version));
        _os = new LogEventProperty("OS", new ScalarValue(RuntimeInformation.OSDescription));
        _copilotCliVersion = new LogEventProperty(
            "CopilotCliVersion",
            new ScalarValue(string.IsNullOrWhiteSpace(copilotCliVersion) ? "unknown" : copilotCliVersion));
    }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        logEvent.AddPropertyIfAbsent(_appVersion);
        logEvent.AddPropertyIfAbsent(_os);
        logEvent.AddPropertyIfAbsent(_copilotCliVersion);
    }
}

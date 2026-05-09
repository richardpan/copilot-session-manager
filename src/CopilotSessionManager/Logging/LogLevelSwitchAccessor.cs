using Serilog.Core;
using Serilog.Events;

namespace CopilotSessionManager.Logging;

/// <summary>
/// Singleton wrapper around the global <see cref="LoggingLevelSwitch"/> so
/// view models can flip the live log level without touching Serilog directly.
/// Registered in DI by <see cref="App"/>.
/// </summary>
public sealed class LogLevelSwitchAccessor
{
    public LogLevelSwitchAccessor(LoggingLevelSwitch levelSwitch)
    {
        ArgumentNullException.ThrowIfNull(levelSwitch);
        Switch = levelSwitch;
    }

    public LoggingLevelSwitch Switch { get; }

    /// <summary>True when the level is currently <see cref="LogEventLevel.Debug"/> or lower.</summary>
    public bool IsVerbose => Switch.MinimumLevel <= LogEventLevel.Debug;

    /// <summary>Sets the live minimum level.</summary>
    public void SetVerbose(bool verbose) =>
        Switch.MinimumLevel = verbose ? LogEventLevel.Debug : LogEventLevel.Information;

    /// <summary>Parse the persisted <c>AppSettings.LogLevel</c> string.</summary>
    public static LogEventLevel ParseLevel(string? value) =>
        string.Equals(value, "Debug", StringComparison.OrdinalIgnoreCase)
            ? LogEventLevel.Debug
            : LogEventLevel.Information;
}

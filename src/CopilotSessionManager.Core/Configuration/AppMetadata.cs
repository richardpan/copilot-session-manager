using System;
using System.Reflection;

namespace CopilotSessionManager.Core.Configuration;

/// <summary>
/// Static, build-time application metadata.
/// </summary>
public static class AppMetadata
{
    /// <summary>The user-visible product name.</summary>
    public const string ProductName = "Copilot Session Manager";

    /// <summary>
    /// Schema version of the user's <c>settings.json</c>.
    /// Bump this when introducing a breaking change to the settings file shape;
    /// add a corresponding migration.
    /// </summary>
    public const int SettingsSchemaVersion = 1;

    /// <summary>
    /// Schema version of the app's local SQLite database.
    /// Bump this when introducing a breaking change to the schema; add a
    /// corresponding migration.
    /// </summary>
    public const int DbSchemaVersion = 1;

    /// <summary>
    /// Minimum supported Copilot CLI version. Older versions will be rejected
    /// at session-load time with a friendly message.
    /// </summary>
    public const string MinSupportedCopilotCliVersion = "1.0.43";

    /// <summary>
    /// The current assembly version, exposed as a string. Reads
    /// <see cref="AssemblyInformationalVersionAttribute"/> when present and
    /// falls back to <see cref="Assembly.GetName"/>.
    /// </summary>
    public static string Version { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var assembly = typeof(AppMetadata).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // SourceLink appends "+<sha>"; strip it for display purposes.
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}

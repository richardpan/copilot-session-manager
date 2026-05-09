using System;
using System.IO;

namespace CopilotSessionManager.Core.Configuration;

/// <summary>
/// Canonical filesystem paths used by the application.
/// </summary>
/// <remarks>
/// All app-owned data lives under <c>%LOCALAPPDATA%\CopilotSessionManager\</c>.
/// We never write inside <c>~/.copilot/</c>; that folder is treated as
/// read-only Copilot CLI state.
/// </remarks>
public static class AppPaths
{
    /// <summary>The folder name used under <c>%LOCALAPPDATA%</c>.</summary>
    public const string AppFolderName = "CopilotSessionManager";

    /// <summary>
    /// Root of the app's local data: <c>%LOCALAPPDATA%\CopilotSessionManager\</c>.
    /// </summary>
    public static string LocalAppDataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppFolderName);

    /// <summary>The directory where rolling Serilog log files are written.</summary>
    public static string LogsDirectory => Path.Combine(LocalAppDataDirectory, "logs");

    /// <summary>The full path to the encrypted app database.</summary>
    public static string AppDatabasePath => Path.Combine(LocalAppDataDirectory, "app.db");

    /// <summary>The DPAPI-protected key blob that unlocks the app database.</summary>
    public static string AppDatabaseKeyPath => Path.Combine(LocalAppDataDirectory, "app-db.key");

    /// <summary>The full path to the user's settings file.</summary>
    public static string SettingsFilePath => Path.Combine(LocalAppDataDirectory, "settings.json");

    /// <summary>
    /// Root of the Copilot CLI's data directory. Read-only from this app.
    /// Defaults to <c>%USERPROFILE%\.copilot\</c>.
    /// </summary>
    public static string CopilotCliDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot");

    /// <summary>The Copilot CLI's global session-store SQLite database.</summary>
    public static string CopilotSessionStoreDatabasePath =>
        Path.Combine(CopilotCliDirectory, "session-store.db");

    /// <summary>The Copilot CLI's per-session state directory.</summary>
    public static string CopilotSessionStateDirectory =>
        Path.Combine(CopilotCliDirectory, "session-state");
}

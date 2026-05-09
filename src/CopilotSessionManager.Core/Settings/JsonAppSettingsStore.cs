using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Settings;

/// <summary>
/// JSON-backed <see cref="IAppSettingsStore"/>. Writes atomically via
/// temp-file-then-replace so a crash mid-write cannot corrupt the file.
/// Runs registered <see cref="IAppSettingsMigration"/>s on load when the
/// on-disk schema version is older than
/// <see cref="AppSettings.CurrentSchemaVersion"/>; if a migration throws,
/// the original file is restored from a sibling backup and defaults are
/// returned to the caller.
/// </summary>
public sealed class JsonAppSettingsStore : IAppSettingsStore
{
    /// <summary>Default file name appended to <c>%LOCALAPPDATA%\CopilotSessionManager\</c>.</summary>
    public const string DefaultFileName = "settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly ILogger<JsonAppSettingsStore> _logger;
    private readonly IReadOnlyList<IAppSettingsMigration> _migrations;
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);

    public JsonAppSettingsStore(string path, ILogger<JsonAppSettingsStore> logger)
        : this(path, logger, migrations: Array.Empty<IAppSettingsMigration>())
    {
    }

    public JsonAppSettingsStore(
        string path,
        ILogger<JsonAppSettingsStore> logger,
        IEnumerable<IAppSettingsMigration> migrations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(migrations);

        _path = path;
        _logger = logger;
        _migrations = migrations.OrderBy(m => m.FromVersion).ToArray();
    }

    /// <summary>The on-disk path this instance reads from / writes to.</summary>
    public string Path => _path;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                return AppSettings.Defaults();
            }

            string content;
            try
            {
                content = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not read settings file at {Path}; using defaults.", _path);
                return AppSettings.Defaults();
            }

            // Detect the on-disk schema version by inspecting the raw JSON: a
            // missing "schemaVersion" key means the file predates schema
            // versioning and must be migrated from v0. We can't rely on the
            // POCO default, since deserialisation leaves the property
            // initializer's value in place when the key is absent.
            int onDiskVersion;
            try
            {
                using var doc = JsonDocument.Parse(content);
                onDiskVersion = doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("schemaVersion", out var v)
                    && v.ValueKind == JsonValueKind.Number
                        ? v.GetInt32()
                        : 0;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Settings file at {Path} is corrupt; using defaults.", _path);
                return AppSettings.Defaults();
            }

            AppSettings loaded;
            try
            {
                loaded = JsonSerializer.Deserialize<AppSettings>(content, JsonOptions)
                    ?? AppSettings.Defaults();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Settings file at {Path} is corrupt; using defaults.", _path);
                return AppSettings.Defaults();
            }

            loaded.SchemaVersion = onDiskVersion;

            if (loaded.SchemaVersion >= AppSettings.CurrentSchemaVersion)
            {
                return loaded;
            }

            return await MigrateAsync(loaded, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var resetBackup = _path + ".reset.bak";
            try
            {
                File.Copy(_path, resetBackup, overwrite: true);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not write reset backup {Backup}; continuing with reset.", resetBackup);
            }

            try
            {
                File.Delete(_path);
                _logger.LogInformation("Settings file {Path} reset; previous content preserved at {Backup}.", _path, resetBackup);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Failed to delete settings file {Path} during reset.", _path);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AppSettings> MigrateAsync(AppSettings loaded, CancellationToken cancellationToken)
    {
        var fromVersion = loaded.SchemaVersion;
        var backupPath = _path + ".bak." + fromVersion.ToString(CultureInfo.InvariantCulture);

        try
        {
            File.Copy(_path, backupPath, overwrite: true);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Could not back up settings before migration; aborting load and returning defaults.");
            return AppSettings.Defaults();
        }

        try
        {
            for (var v = fromVersion; v < AppSettings.CurrentSchemaVersion; v++)
            {
                var migration = _migrations.FirstOrDefault(m => m.FromVersion == v);
                if (migration is null)
                {
                    _logger.LogWarning(
                        "No migration registered for settings schema {From} -> {To}; stamping target version without changes.",
                        v, v + 1);
                }
                else
                {
                    _logger.LogInformation(
                        "Migrating settings schema {From} -> {To} via {Migration}.",
                        v, v + 1, migration.GetType().Name);
                    migration.Apply(loaded);
                }

                loaded.SchemaVersion = v + 1;
            }

            await SaveCoreAsync(loaded, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Settings migrated from v{From} to v{To}. Backup at {Backup}.",
                fromVersion, AppSettings.CurrentSchemaVersion, backupPath);
            return loaded;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Settings migration failed; restoring backup from {Backup}.", backupPath);
            try
            {
                File.Copy(backupPath, _path, overwrite: true);
            }
            catch (IOException restoreEx)
            {
                _logger.LogError(restoreEx, "Could not restore settings backup; user must intervene manually.");
            }
            return AppSettings.Defaults();
        }
    }

    private async Task SaveCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Always stamp the current schema version on save so a SaveAsync of
        // an in-memory instance produced from a legacy load can never
        // regress to an older shape on disk.
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;

        // Atomic replace via temp file so a crash mid-write doesn't
        // leave the settings in an unparseable half-state.
        var tempPath = _path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        if (File.Exists(_path))
        {
            File.Replace(tempPath, _path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, _path);
        }
    }
}

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Settings;

/// <summary>
/// JSON-backed <see cref="IAppSettingsStore"/>. Writes atomically via
/// temp-file-then-replace so a crash mid-write cannot corrupt the file.
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
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);

    public JsonAppSettingsStore(string path, ILogger<JsonAppSettingsStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);
        _path = path;
        _logger = logger;
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

            try
            {
                await using var stream = File.OpenRead(_path);
                var loaded = await JsonSerializer.DeserializeAsync<AppSettings>(
                    stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                return loaded ?? AppSettings.Defaults();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Settings file at {Path} is corrupt; using defaults.", _path);
                return AppSettings.Defaults();
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not read settings file at {Path}; using defaults.", _path);
                return AppSettings.Defaults();
            }
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
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

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
        finally
        {
            _gate.Release();
        }
    }
}

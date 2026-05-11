using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// File-backed implementation of <see cref="ISessionDisplayNameStore"/> (#105).
/// Mirrors the <see cref="JsonSessionLabelStore"/> pattern: a single small
/// JSON document under <c>%LOCALAPPDATA%\CopilotSessionManager\</c>, atomic
/// write-temp-then-rename, in-memory cache.
/// </summary>
public sealed class JsonSessionDisplayNameStore : ISessionDisplayNameStore
{
    /// <summary>Default file name (relative to <see cref="AppPaths.LocalAppDataDirectory"/>).</summary>
    public const string DefaultFileName = "display-names.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _filePath;
    private readonly ILogger<JsonSessionDisplayNameStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Dictionary<string, string>? _cache;

    public JsonSessionDisplayNameStore(string filePath, ILogger<JsonSessionDisplayNameStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(logger);

        _filePath = filePath;
        _logger = logger;
    }

    public event EventHandler<SessionDisplayNameChangedEventArgs>? DisplayNameChanged;

    public async Task<string?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var cache = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return cache.TryGetValue(sessionId, out var name) ? name : null;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var cache = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return new Dictionary<string, string>(cache, StringComparer.OrdinalIgnoreCase);
    }

    public async Task SetAsync(
        string sessionId,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (string.IsNullOrWhiteSpace(displayName))
        {
            await RemoveAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return;
        }

        var trimmed = displayName.Trim();
        var cache = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        bool changed;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            changed = !cache.TryGetValue(sessionId, out var existing)
                      || !string.Equals(existing, trimmed, StringComparison.Ordinal);
            if (!changed)
            {
                return;
            }

            cache[sessionId] = trimmed;
            await PersistAsync(cache, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        DisplayNameChanged?.Invoke(this, new SessionDisplayNameChangedEventArgs(sessionId, trimmed));
    }

    public async Task RemoveAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var cache = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        bool removed;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            removed = cache.Remove(sessionId);
            if (!removed)
            {
                return;
            }
            await PersistAsync(cache, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        DisplayNameChanged?.Invoke(this, new SessionDisplayNameChangedEventArgs(sessionId, null));
    }

    private async Task<Dictionary<string, string>> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _cache ??= await LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            await using var stream = new FileStream(
                _filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var doc = await JsonSerializer
                .DeserializeAsync<DisplayNamesDocument>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (doc?.DisplayNames is null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return new Dictionary<string, string>(doc.DisplayNames, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(
                ex,
                "Failed to read display-names file at {Path}; backing it up and starting fresh.",
                _filePath);

            TryBackupCorruptFile();
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task PersistAsync(
        IReadOnlyDictionary<string, string> displayNames,
        CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var doc = new DisplayNamesDocument
        {
            Version = 1,
            DisplayNames = new Dictionary<string, string>(displayNames, StringComparer.OrdinalIgnoreCase),
        };

        var tempPath = _filePath + ".tmp";
        await using (var stream = new FileStream(
            tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer
                .SerializeAsync(stream, doc, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, _filePath, overwrite: true);
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            var backup = _filePath + ".bak." + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            File.Move(_filePath, backup);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not back up corrupt display-names file at {Path}.", _filePath);
        }
    }

    private sealed class DisplayNamesDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("displayNames")]
        public Dictionary<string, string>? DisplayNames { get; set; }
    }
}

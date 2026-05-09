using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Configuration;
using CopilotSessionManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// File-backed implementation of <see cref="ISessionLabelStore"/>. Persists a
/// single small JSON document under <c>%LOCALAPPDATA%\CopilotSessionManager\</c>
/// using the atomic write-temp-then-rename pattern.
/// </summary>
public sealed class JsonSessionLabelStore : ISessionLabelStore
{
    /// <summary>Default file name (relative to
    /// <see cref="AppPaths.LocalAppDataDirectory"/>).</summary>
    public const string DefaultFileName = "labels.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;
    private readonly ILogger<JsonSessionLabelStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Dictionary<string, SessionType>? _cache;

    public JsonSessionLabelStore(string filePath, ILogger<JsonSessionLabelStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(logger);

        _filePath = filePath;
        _logger = logger;
    }

    public event EventHandler<SessionLabelChangedEventArgs>? LabelChanged;

    public async Task<SessionType> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var cache = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return cache.TryGetValue(sessionId, out var t) ? t : SessionType.Exploratory;
    }

    public async Task<IReadOnlyDictionary<string, SessionType>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var cache = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return new Dictionary<string, SessionType>(cache, StringComparer.OrdinalIgnoreCase);
    }

    public async Task SetAsync(string sessionId, SessionType type, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var cache = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        bool changed;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            changed = !cache.TryGetValue(sessionId, out var existing) || existing != type;
            if (!changed)
            {
                return;
            }

            cache[sessionId] = type;
            await PersistAsync(cache, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        LabelChanged?.Invoke(this, new SessionLabelChangedEventArgs(sessionId, type));
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

        LabelChanged?.Invoke(this, new SessionLabelChangedEventArgs(sessionId, SessionType.Exploratory));
    }

    private async Task<Dictionary<string, SessionType>> EnsureLoadedAsync(CancellationToken cancellationToken)
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

    private async Task<Dictionary<string, SessionType>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, SessionType>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            await using var stream = new FileStream(
                _filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var doc = await JsonSerializer
                .DeserializeAsync<LabelsDocument>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (doc?.Labels is null)
            {
                return new Dictionary<string, SessionType>(StringComparer.OrdinalIgnoreCase);
            }

            return new Dictionary<string, SessionType>(doc.Labels, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(
                ex,
                "Failed to read labels file at {Path}; backing it up and starting fresh.",
                _filePath);

            TryBackupCorruptFile();
            return new Dictionary<string, SessionType>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task PersistAsync(
        IReadOnlyDictionary<string, SessionType> labels,
        CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var doc = new LabelsDocument
        {
            Version = 1,
            Labels = new Dictionary<string, SessionType>(labels, StringComparer.OrdinalIgnoreCase),
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

        // File.Move(overwrite: true) is atomic on the same volume.
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
            _logger.LogDebug(ex, "Could not back up corrupt labels file at {Path}.", _filePath);
        }
    }

    private sealed class LabelsDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("labels")]
        public Dictionary<string, SessionType>? Labels { get; set; }
    }
}

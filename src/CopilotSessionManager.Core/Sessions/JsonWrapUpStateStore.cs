using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// File-backed implementation of <see cref="IWrapUpStateStore"/> (#149).
/// Mirrors <see cref="JsonSessionStarStore"/>: a single small JSON
/// document under <c>%LOCALAPPDATA%\CopilotSessionManager\</c>, atomic
/// write-temp-then-rename, in-memory cache.
/// </summary>
public sealed class JsonWrapUpStateStore : IWrapUpStateStore
{
    /// <summary>Default file name (relative to <c>AppPaths.LocalAppDataDirectory</c>).</summary>
    public const string DefaultFileName = "wrapup.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _filePath;
    private readonly ILogger<JsonWrapUpStateStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Dictionary<string, DateTimeOffset>? _cache;

    public JsonWrapUpStateStore(string filePath, ILogger<JsonWrapUpStateStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(logger);

        _filePath = filePath;
        _logger = logger;
    }

    public event EventHandler<WrapUpStateChangedEventArgs>? WrapUpStateChanged;

    public async Task<DateTimeOffset?> GetRequestedAtAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var cache = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return cache.TryGetValue(sessionId, out var ts) ? ts : null;
    }

    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var cache = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return new Dictionary<string, DateTimeOffset>(cache, StringComparer.OrdinalIgnoreCase);
    }

    public async Task MarkRequestedAsync(string sessionId, DateTimeOffset requestedAt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var cache = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cache[sessionId] = requestedAt;
            await PersistAsync(cache, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        WrapUpStateChanged?.Invoke(this, new WrapUpStateChangedEventArgs(sessionId, requestedAt));
    }

    public async Task ClearAsync(string sessionId, CancellationToken cancellationToken = default)
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

        WrapUpStateChanged?.Invoke(this, new WrapUpStateChangedEventArgs(sessionId, requestedAt: null));
    }

    private async Task<Dictionary<string, DateTimeOffset>> EnsureLoadedAsync(CancellationToken cancellationToken)
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

    private async Task<Dictionary<string, DateTimeOffset>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            await using var stream = new FileStream(
                _filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var doc = await JsonSerializer
                .DeserializeAsync<WrapUpDocument>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
            if (doc?.Requested is null)
            {
                return result;
            }

            foreach (var kv in doc.Requested)
            {
                if (string.IsNullOrWhiteSpace(kv.Key))
                {
                    continue;
                }
                result[kv.Key] = kv.Value;
            }

            return result;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(
                ex,
                "Failed to read wrap-up state file at {Path}; backing it up and starting fresh.",
                _filePath);

            TryBackupCorruptFile();
            return new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task PersistAsync(IReadOnlyDictionary<string, DateTimeOffset> requested, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Sorted keys for stable on-disk ordering — friendlier diffs.
        var ordered = requested
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        var doc = new WrapUpDocument
        {
            Version = 1,
            Requested = ordered,
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
            _logger.LogDebug(ex, "Could not back up corrupt wrap-up state file at {Path}.", _filePath);
        }
    }

    private sealed class WrapUpDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("requested")]
        public Dictionary<string, DateTimeOffset>? Requested { get; set; }
    }
}

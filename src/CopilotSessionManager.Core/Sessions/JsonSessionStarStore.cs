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
/// File-backed implementation of <see cref="ISessionStarStore"/> (#112).
/// Mirrors <see cref="JsonSessionDisplayNameStore"/>: a single small JSON
/// document under <c>%LOCALAPPDATA%\CopilotSessionManager\</c>, atomic
/// write-temp-then-rename, in-memory cache.
/// </summary>
public sealed class JsonSessionStarStore : ISessionStarStore
{
    /// <summary>Default file name (relative to <c>AppPaths.LocalAppDataDirectory</c>).</summary>
    public const string DefaultFileName = "stars.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _filePath;
    private readonly ILogger<JsonSessionStarStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private HashSet<string>? _cache;

    public JsonSessionStarStore(string filePath, ILogger<JsonSessionStarStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(logger);

        _filePath = filePath;
        _logger = logger;
    }

    public event EventHandler<SessionStarChangedEventArgs>? StarsChanged;

    public async Task<bool> IsStarredAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var cache = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return cache.Contains(sessionId);
    }

    public async Task<IReadOnlySet<string>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var cache = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return new HashSet<string>(cache, StringComparer.OrdinalIgnoreCase);
    }

    public async Task SetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var cache = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        bool added;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            added = cache.Add(sessionId);
            if (!added)
            {
                return;
            }
            await PersistAsync(cache, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        StarsChanged?.Invoke(this, new SessionStarChangedEventArgs(sessionId, isStarred: true));
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

        StarsChanged?.Invoke(this, new SessionStarChangedEventArgs(sessionId, isStarred: false));
    }

    private async Task<HashSet<string>> EnsureLoadedAsync(CancellationToken cancellationToken)
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

    private async Task<HashSet<string>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            await using var stream = new FileStream(
                _filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var doc = await JsonSerializer
                .DeserializeAsync<StarsDocument>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (doc?.Stars is null)
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            return new HashSet<string>(
                doc.Stars.Where(s => !string.IsNullOrWhiteSpace(s)),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(
                ex,
                "Failed to read stars file at {Path}; backing it up and starting fresh.",
                _filePath);

            TryBackupCorruptFile();
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task PersistAsync(IReadOnlyCollection<string> stars, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Sort for stable on-disk ordering — makes diffs / debugging cleaner.
        var doc = new StarsDocument
        {
            Version = 1,
            Stars = stars.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
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
            _logger.LogDebug(ex, "Could not back up corrupt stars file at {Path}.", _filePath);
        }
    }

    private sealed class StarsDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("stars")]
        public List<string>? Stars { get; set; }
    }
}

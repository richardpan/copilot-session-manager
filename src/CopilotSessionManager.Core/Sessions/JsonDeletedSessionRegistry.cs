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
/// File-backed <see cref="IDeletedSessionRegistry"/> (#125). A single small
/// JSON document under <c>%LOCALAPPDATA%\CopilotSessionManager\</c>, atomic
/// write-temp-then-rename, in-memory cache. Mirrors
/// <see cref="JsonSessionStarStore"/>.
/// </summary>
public sealed class JsonDeletedSessionRegistry : IDeletedSessionRegistry
{
    /// <summary>Default file name (relative to <c>AppPaths.LocalAppDataDirectory</c>).</summary>
    public const string DefaultFileName = "deleted-sessions.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _filePath;
    private readonly ILogger<JsonDeletedSessionRegistry> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private HashSet<string>? _cache;

    public JsonDeletedSessionRegistry(string filePath, ILogger<JsonDeletedSessionRegistry> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(logger);

        _filePath = filePath;
        _logger = logger;
    }

    public async Task<bool> IsDeletedAsync(string sessionId, CancellationToken cancellationToken = default)
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

    public async Task RecordAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var cache = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!cache.Add(sessionId))
            {
                return;
            }
            await PersistAsync(cache, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ForgetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var cache = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!cache.Remove(sessionId))
            {
                return;
            }
            await PersistAsync(cache, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
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
                .DeserializeAsync<DeletedSessionsDocument>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (doc?.Ids is null)
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            return new HashSet<string>(
                doc.Ids.Where(s => !string.IsNullOrWhiteSpace(s)),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(
                ex,
                "Failed to read deleted-sessions registry at {Path}; backing it up and starting fresh.",
                _filePath);

            TryBackupCorruptFile();
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task PersistAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Sort for stable on-disk ordering — makes diffs / debugging cleaner.
        var doc = new DeletedSessionsDocument
        {
            Version = 1,
            Ids = ids.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
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
            _logger.LogDebug(ex, "Could not back up corrupt deleted-sessions file at {Path}.", _filePath);
        }
    }

    private sealed class DeletedSessionsDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("deletedIds")]
        public List<string>? Ids { get; set; }
    }
}

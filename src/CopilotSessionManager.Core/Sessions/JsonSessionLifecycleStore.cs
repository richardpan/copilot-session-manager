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
/// File-backed implementation of <see cref="ISessionLifecycleStore"/>.
/// Mirrors <see cref="JsonSessionStarStore"/>: a single small JSON document
/// under <c>%LOCALAPPDATA%\CopilotSessionManager\</c>, atomic
/// write-temp-then-rename, in-memory cache. Only stores session ids that are
/// explicitly Closed — "not present" implies Active.
/// </summary>
public sealed class JsonSessionLifecycleStore : ISessionLifecycleStore
{
    /// <summary>Default file name (relative to <c>AppPaths.LocalAppDataDirectory</c>).</summary>
    public const string DefaultFileName = "lifecycle.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _filePath;
    private readonly ILogger<JsonSessionLifecycleStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private HashSet<string>? _closedCache;

    public JsonSessionLifecycleStore(string filePath, ILogger<JsonSessionLifecycleStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(logger);

        _filePath = filePath;
        _logger = logger;
    }

    public event EventHandler<SessionLifecycleChangedEventArgs>? LifecycleChanged;

    public async Task<SessionLifecycleState> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var cache = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return cache.Contains(sessionId) ? SessionLifecycleState.Closed : SessionLifecycleState.Open;
    }

    public async Task<IReadOnlySet<string>> GetClosedAsync(CancellationToken cancellationToken = default)
    {
        var cache = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return new HashSet<string>(cache, StringComparer.OrdinalIgnoreCase);
    }

    public async Task SetAsync(string sessionId, SessionLifecycleState state, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var cache = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        bool changed;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            changed = state == SessionLifecycleState.Closed
                ? cache.Add(sessionId)
                : cache.Remove(sessionId);
            if (!changed)
            {
                return;
            }
            await PersistAsync(cache, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        LifecycleChanged?.Invoke(this, new SessionLifecycleChangedEventArgs(sessionId, state));
    }

    private async Task<HashSet<string>> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_closedCache is not null)
        {
            return _closedCache;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _closedCache ??= await LoadAsync(cancellationToken).ConfigureAwait(false);
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
                .DeserializeAsync<LifecycleDocument>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (doc?.Closed is null)
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            return new HashSet<string>(
                doc.Closed.Where(s => !string.IsNullOrWhiteSpace(s)),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(
                ex,
                "Failed to read lifecycle file at {Path}; backing it up and starting fresh.",
                _filePath);

            TryBackupCorruptFile();
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task PersistAsync(IReadOnlyCollection<string> closed, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var doc = new LifecycleDocument
        {
            Version = 1,
            Closed = closed.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
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
            _logger.LogDebug(ex, "Could not back up corrupt lifecycle file at {Path}.", _filePath);
        }
    }

    private sealed class LifecycleDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        /// <summary>Session ids the user has marked as Closed.</summary>
        [JsonPropertyName("closed")]
        public List<string>? Closed { get; set; }
    }
}

using System.Collections.Concurrent;
using CopilotSessionManager.Core.Cli;
using CopilotSessionManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Sessions;

/// <inheritdoc />
public sealed class SessionDiscoveryService : ISessionDiscoveryService
{
    private const string WorkspaceFileName = "workspace.yaml";
    private const string EventsFileName = "events.jsonl";

    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(400);

    private readonly ISessionStore _store;
    private readonly ICopilotCliAdapterRegistry _adapterRegistry;
    private readonly ICopilotPaths _paths;
    private readonly ILogger<SessionDiscoveryService> _logger;

    private readonly object _watcherLock = new();
    private readonly SemaphoreSlim _scanSemaphore = new(1, 1);

    private FileSystemWatcher? _databaseWatcher;
    private FileSystemWatcher? _stateDirectoryWatcher;
    private CancellationTokenSource? _debounceCts;
    private volatile IReadOnlyList<Session> _currentSessions = Array.Empty<Session>();
    private bool _disposed;

    public SessionDiscoveryService(
        ISessionStore store,
        ICopilotCliAdapterRegistry adapterRegistry,
        ICopilotPaths paths,
        ILogger<SessionDiscoveryService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(adapterRegistry);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _adapterRegistry = adapterRegistry;
        _paths = paths;
        _logger = logger;
    }

    public IReadOnlyList<Session> CurrentSessions => _currentSessions;

    public event EventHandler<SessionsChangedEventArgs>? SessionsChanged;

    public async Task<IReadOnlyList<Session>> ScanAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _scanSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var storeRecords = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
            var stateIds = EnumerateStateDirectoryIds();

            var byId = new Dictionary<string, SessionStoreRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in storeRecords)
            {
                byId[record.Id] = record;
            }

            var allIds = new HashSet<string>(byId.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var id in stateIds)
            {
                allIds.Add(id);
            }

            var sessions = new List<Session>(allIds.Count);
            foreach (var id in allIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var session = await BuildSessionAsync(id, byId, cancellationToken).ConfigureAwait(false);
                if (session is not null)
                {
                    sessions.Add(session);
                }
            }

            sessions.Sort(static (a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));

            _currentSessions = sessions;
            return sessions;
        }
        finally
        {
            _scanSemaphore.Release();
        }
    }

    public async Task StartWatchingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await ScanAsync(cancellationToken).ConfigureAwait(false);

        lock (_watcherLock)
        {
            if (_databaseWatcher is not null || _stateDirectoryWatcher is not null)
            {
                return;
            }

            var dbPath = _paths.SessionStoreDatabasePath;
            var dbDir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dbDir) && Directory.Exists(dbDir))
            {
                _databaseWatcher = new FileSystemWatcher(dbDir, Path.GetFileName(dbPath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true,
                };
                _databaseWatcher.Changed += OnFilesystemChanged;
                _databaseWatcher.Created += OnFilesystemChanged;
                _databaseWatcher.Renamed += OnFilesystemChanged;
                _databaseWatcher.Error += OnWatcherError;
            }

            var stateDir = _paths.SessionStateDirectory;
            if (Directory.Exists(stateDir))
            {
                _stateDirectoryWatcher = new FileSystemWatcher(stateDir)
                {
                    NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true,
                };
                _stateDirectoryWatcher.Created += OnFilesystemChanged;
                _stateDirectoryWatcher.Deleted += OnFilesystemChanged;
                _stateDirectoryWatcher.Renamed += OnFilesystemChanged;
                _stateDirectoryWatcher.Error += OnWatcherError;
            }
        }
    }

    public Task StopWatchingAsync()
    {
        DisposeWatchers();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeWatchers();
        _scanSemaphore.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void DisposeWatchers()
    {
        lock (_watcherLock)
        {
            if (_databaseWatcher is not null)
            {
                _databaseWatcher.EnableRaisingEvents = false;
                _databaseWatcher.Changed -= OnFilesystemChanged;
                _databaseWatcher.Created -= OnFilesystemChanged;
                _databaseWatcher.Renamed -= OnFilesystemChanged;
                _databaseWatcher.Error -= OnWatcherError;
                _databaseWatcher.Dispose();
                _databaseWatcher = null;
            }

            if (_stateDirectoryWatcher is not null)
            {
                _stateDirectoryWatcher.EnableRaisingEvents = false;
                _stateDirectoryWatcher.Created -= OnFilesystemChanged;
                _stateDirectoryWatcher.Deleted -= OnFilesystemChanged;
                _stateDirectoryWatcher.Renamed -= OnFilesystemChanged;
                _stateDirectoryWatcher.Error -= OnWatcherError;
                _stateDirectoryWatcher.Dispose();
                _stateDirectoryWatcher = null;
            }

            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }
    }

    private void OnFilesystemChanged(object sender, FileSystemEventArgs e)
    {
        ScheduleRescan();
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.LogWarning(e.GetException(), "Filesystem watcher error in session discovery; continuing.");
    }

    private void ScheduleRescan()
    {
        CancellationTokenSource debounceCts;
        lock (_watcherLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            debounceCts = _debounceCts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceInterval, debounceCts.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            try
            {
                var sessions = await ScanAsync(CancellationToken.None).ConfigureAwait(false);
                SessionsChanged?.Invoke(this, new SessionsChangedEventArgs(sessions));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Rescan failed after filesystem change.");
            }
        });
    }

    private IEnumerable<string> EnumerateStateDirectoryIds()
    {
        var root = _paths.SessionStateDirectory;
        if (!Directory.Exists(root))
        {
            return Array.Empty<string>();
        }

        return Directory
            .EnumerateDirectories(root)
            .Select(static d => Path.GetFileName(d.TrimEnd(Path.DirectorySeparatorChar)))
            .Where(static name => !string.IsNullOrEmpty(name))
            .Cast<string>();
    }

    private async Task<Session?> BuildSessionAsync(
        string id,
        IReadOnlyDictionary<string, SessionStoreRecord> records,
        CancellationToken cancellationToken)
    {
        records.TryGetValue(id, out var record);

        var sessionDir = Path.Combine(_paths.SessionStateDirectory, id);
        var hasStateDir = Directory.Exists(sessionDir);

        var version = await TryReadCopilotVersionAsync(sessionDir, hasStateDir, cancellationToken)
            .ConfigureAwait(false);
        var workspace = TryParseWorkspace(sessionDir, hasStateDir, version);

        if (record is null && workspace is null)
        {
            _logger.LogDebug(
                "Session {SessionId} has neither a DB row nor a workspace.yaml; skipping.",
                id);
            return null;
        }

        var createdAt = record?.CreatedAt ?? workspace?.CreatedAt ?? DateTimeOffset.MinValue;
        var updatedAt = record?.UpdatedAt ?? workspace?.UpdatedAt ?? DateTimeOffset.MinValue;

        // Status detection (lock + events) lands in a follow-up PR; for now,
        // every discovered session starts as Inactive.
        var status = SessionStatus.Inactive;

        return new Session(
            Id: id,
            Cwd: record?.Cwd ?? workspace?.Cwd,
            Repository: record?.Repository ?? workspace?.Repository,
            Branch: record?.Branch ?? workspace?.Branch,
            Summary: record?.Summary ?? workspace?.Summary,
            HostType: record?.HostType ?? workspace?.HostType,
            CreatedAt: createdAt,
            UpdatedAt: updatedAt,
            TurnCount: record?.TurnCount ?? 0,
            Status: status,
            CopilotVersion: version);
    }

    private async Task<CopilotVersion> TryReadCopilotVersionAsync(
        string sessionDir,
        bool hasStateDir,
        CancellationToken cancellationToken)
    {
        if (!hasStateDir)
        {
            return CopilotVersion.Zero;
        }

        var eventsPath = Path.Combine(sessionDir, EventsFileName);
        if (!File.Exists(eventsPath))
        {
            return CopilotVersion.Zero;
        }

        try
        {
            await using var stream = new FileStream(
                eventsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var version = await _adapterRegistry.Latest
                .ReadCopilotVersionAsync(stream, cancellationToken)
                .ConfigureAwait(false);

            return version ?? CopilotVersion.Zero;
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not read events.jsonl at {Path}.", eventsPath);
            return CopilotVersion.Zero;
        }
    }

    private WorkspaceManifest? TryParseWorkspace(
        string sessionDir,
        bool hasStateDir,
        CopilotVersion version)
    {
        if (!hasStateDir)
        {
            return null;
        }

        var workspacePath = Path.Combine(sessionDir, WorkspaceFileName);
        if (!File.Exists(workspacePath))
        {
            return null;
        }

        var adapter = version == CopilotVersion.Zero
            ? _adapterRegistry.Latest
            : _adapterRegistry.Resolve(version).Adapter;

        try
        {
            var yaml = File.ReadAllText(workspacePath);
            return adapter.ParseWorkspace(yaml);
        }
        catch (Exception ex) when (ex is IOException or FormatException)
        {
            _logger.LogWarning(ex, "Could not parse workspace.yaml at {Path}.", workspacePath);
            return null;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

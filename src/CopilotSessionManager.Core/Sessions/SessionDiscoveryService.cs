using CopilotSessionManager.Core.Cli;
using CopilotSessionManager.Core.GitHub;
using CopilotSessionManager.Core.GitHub.Storage;
using CopilotSessionManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Sessions;

/// <inheritdoc />
public sealed class SessionDiscoveryService : ISessionDiscoveryService
{
    private const string WorkspaceFileName = "workspace.yaml";
    private const string EventsFileName = "events.jsonl";
    private const string LockFilePattern = "inuse.*.lock";

    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(400);

    private readonly ISessionStore _store;
    private readonly ICopilotCliAdapterRegistry _adapterRegistry;
    private readonly ICopilotPaths _paths;
    private readonly ISessionLockMonitor _lockMonitor;
    private readonly ISessionStatusEvaluator _statusEvaluator;
    private readonly IGitHubLinkResolver _githubLinkResolver;
    private readonly ISessionGitHubLinksStore? _githubLinksOverrideStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SessionDiscoveryService> _logger;

    private readonly object _watcherLock = new();
    private readonly SemaphoreSlim _scanSemaphore = new(1, 1);

    private FileSystemWatcher? _databaseWatcher;
    private FileSystemWatcher? _stateDirectoryWatcher;
    private FileSystemWatcher? _stateContentsWatcher;
    private CancellationTokenSource? _debounceCts;
    private volatile IReadOnlyList<Session> _currentSessions = Array.Empty<Session>();
    private bool _disposed;

    public SessionDiscoveryService(
        ISessionStore store,
        ICopilotCliAdapterRegistry adapterRegistry,
        ICopilotPaths paths,
        ISessionLockMonitor lockMonitor,
        ISessionStatusEvaluator statusEvaluator,
        ILogger<SessionDiscoveryService> logger)
        : this(store, adapterRegistry, paths, lockMonitor, statusEvaluator,
            githubLinkResolver: new GitHubLinkResolver(), githubLinksOverrideStore: null,
            TimeProvider.System, logger)
    {
    }

    public SessionDiscoveryService(
        ISessionStore store,
        ICopilotCliAdapterRegistry adapterRegistry,
        ICopilotPaths paths,
        ISessionLockMonitor lockMonitor,
        ISessionStatusEvaluator statusEvaluator,
        IGitHubLinkResolver githubLinkResolver,
        ILogger<SessionDiscoveryService> logger)
        : this(store, adapterRegistry, paths, lockMonitor, statusEvaluator,
            githubLinkResolver, githubLinksOverrideStore: null, TimeProvider.System, logger)
    {
    }

    public SessionDiscoveryService(
        ISessionStore store,
        ICopilotCliAdapterRegistry adapterRegistry,
        ICopilotPaths paths,
        ISessionLockMonitor lockMonitor,
        ISessionStatusEvaluator statusEvaluator,
        TimeProvider timeProvider,
        ILogger<SessionDiscoveryService> logger)
        : this(store, adapterRegistry, paths, lockMonitor, statusEvaluator,
            githubLinkResolver: new GitHubLinkResolver(), githubLinksOverrideStore: null,
            timeProvider, logger)
    {
    }

    public SessionDiscoveryService(
        ISessionStore store,
        ICopilotCliAdapterRegistry adapterRegistry,
        ICopilotPaths paths,
        ISessionLockMonitor lockMonitor,
        ISessionStatusEvaluator statusEvaluator,
        IGitHubLinkResolver githubLinkResolver,
        TimeProvider timeProvider,
        ILogger<SessionDiscoveryService> logger)
        : this(store, adapterRegistry, paths, lockMonitor, statusEvaluator,
            githubLinkResolver, githubLinksOverrideStore: null, timeProvider, logger)
    {
    }

    /// <summary>
    /// Primary constructor used by DI. Wires the optional
    /// <see cref="ISessionGitHubLinksStore"/> so user-supplied repository /
    /// branch / pull-request overrides are overlaid onto the auto-detected
    /// links during <see cref="ScanAsync"/>.
    /// </summary>
    public SessionDiscoveryService(
        ISessionStore store,
        ICopilotCliAdapterRegistry adapterRegistry,
        ICopilotPaths paths,
        ISessionLockMonitor lockMonitor,
        ISessionStatusEvaluator statusEvaluator,
        IGitHubLinkResolver githubLinkResolver,
        ISessionGitHubLinksStore? githubLinksOverrideStore,
        TimeProvider timeProvider,
        ILogger<SessionDiscoveryService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(adapterRegistry);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(lockMonitor);
        ArgumentNullException.ThrowIfNull(statusEvaluator);
        ArgumentNullException.ThrowIfNull(githubLinkResolver);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _adapterRegistry = adapterRegistry;
        _paths = paths;
        _lockMonitor = lockMonitor;
        _statusEvaluator = statusEvaluator;
        _githubLinkResolver = githubLinkResolver;
        _githubLinksOverrideStore = githubLinksOverrideStore;
        _timeProvider = timeProvider;
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

                // Watch lock files + events.jsonl across every session
                // subdirectory so status changes (and orphaned lock cleanup)
                // trigger a rescan.
                _stateContentsWatcher = new FileSystemWatcher(stateDir)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true,
                };
                _stateContentsWatcher.Filter = "*.*";
                _stateContentsWatcher.Created += OnSessionContentChanged;
                _stateContentsWatcher.Changed += OnSessionContentChanged;
                _stateContentsWatcher.Deleted += OnSessionContentChanged;
                _stateContentsWatcher.Renamed += OnSessionContentChanged;
                _stateContentsWatcher.Error += OnWatcherError;
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

            if (_stateContentsWatcher is not null)
            {
                _stateContentsWatcher.EnableRaisingEvents = false;
                _stateContentsWatcher.Created -= OnSessionContentChanged;
                _stateContentsWatcher.Changed -= OnSessionContentChanged;
                _stateContentsWatcher.Deleted -= OnSessionContentChanged;
                _stateContentsWatcher.Renamed -= OnSessionContentChanged;
                _stateContentsWatcher.Error -= OnWatcherError;
                _stateContentsWatcher.Dispose();
                _stateContentsWatcher = null;
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

    private void OnSessionContentChanged(object sender, FileSystemEventArgs e)
    {
        // Only react to lock files and events.jsonl — ignore noisy SQLite WAL
        // chatter, workspace.yaml saves from non-status edits, etc.
        var name = Path.GetFileName(e.Name) ?? string.Empty;
        if (name.Length == 0)
        {
            return;
        }

        var isLock = name.StartsWith("inuse.", StringComparison.OrdinalIgnoreCase) &&
                     name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);
        var isEvents = string.Equals(name, EventsFileName, StringComparison.OrdinalIgnoreCase);
        if (!isLock && !isEvents)
        {
            return;
        }

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
        var modelInfo = await TryReadModelInfoAsync(sessionDir, hasStateDir, version, cancellationToken)
            .ConfigureAwait(false);
        var producer = await TryReadProducerAsync(sessionDir, hasStateDir, cancellationToken)
            .ConfigureAwait(false);

        if (record is null && workspace is null)
        {
            _logger.LogDebug(
                "Session {SessionId} has neither a DB row nor a workspace.yaml; skipping.",
                id);
            return null;
        }

        var createdAt = record?.CreatedAt ?? workspace?.CreatedAt ?? DateTimeOffset.MinValue;
        var updatedAt = record?.UpdatedAt ?? workspace?.UpdatedAt ?? DateTimeOffset.MinValue;

        var locks = _lockMonitor.GetLocks(id);
        var status = await _statusEvaluator
            .EvaluateAsync(id, locks, version, _timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);

        var baseLinks = ResolveGitHubLinksFor(
            record?.Repository ?? workspace?.Repository,
            record?.Branch ?? workspace?.Branch);
        var links = await ApplyGitHubLinkOverridesAsync(id, baseLinks, cancellationToken)
            .ConfigureAwait(false);

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
            CopilotVersion: version,
            Locks: locks,
            ModelInfo: modelInfo,
            GitHubLinks: links,
            Producer: producer);
    }

    private async Task<SessionGitHubLinks> ApplyGitHubLinkOverridesAsync(
        string sessionId,
        SessionGitHubLinks baseLinks,
        CancellationToken cancellationToken)
    {
        if (_githubLinksOverrideStore is null)
        {
            return baseLinks;
        }

        SessionGitHubLinkOverrides? overrides;
        try
        {
            overrides = await _githubLinksOverrideStore
                .GetAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Defensive: the store contracts to never throw out of GetAsync,
            // but if a buggy implementation does we keep discovery working
            // with the un-overridden links rather than failing the scan.
            _logger.LogWarning(
                ex,
                "Failed to read GitHub link overrides for {SessionId}; using auto-detected links.",
                sessionId);
            return baseLinks;
        }

        if (overrides is null || !overrides.HasAnyOverride)
        {
            return baseLinks;
        }

        var repoUrl = baseLinks.RepositoryUrl;
        var branchUrl = baseLinks.BranchUrl;
        var pr = baseLinks.PullRequest;

        if (overrides.RepositoryOverride is { } repoOverride)
        {
            repoUrl = NormalizeRepositoryOverrideToUrl(repoOverride);
        }

        if (overrides.BranchOverride is { } branchOverride)
        {
            branchUrl = branchOverride;
        }

        if (overrides.PullRequestNumberOverride is { } prNumber && repoUrl is not null)
        {
            // The user told us a PR number but we don't necessarily know its
            // title or state yet — surface a placeholder PullRequestInfo so
            // the link is clickable, and let the live PR enrichment pipeline
            // (#69) refine it later.
            pr = new PullRequestInfo(
                Number: prNumber,
                Title: pr?.Title ?? string.Empty,
                State: pr?.State ?? PullRequestState.Open,
                Url: $"{repoUrl}/pull/{prNumber}");
        }

        return new SessionGitHubLinks(repoUrl, branchUrl, pr);
    }

    private static string? NormalizeRepositoryOverrideToUrl(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        // Already an absolute http(s) URL — accept as-is.
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.TrimEnd('/');
        }

        // Otherwise treat as an owner/name slug.
        return $"https://github.com/{trimmed.TrimEnd('/')}";
    }

    private SessionGitHubLinks ResolveGitHubLinksFor(string? repository, string? branch)
    {
        // Build a lightweight Session shell so the resolver can stay pure
        // (it only reads Repository + Branch).
        var shell = new Session(
            Id: string.Empty,
            Cwd: null,
            Repository: repository,
            Branch: branch,
            Summary: null,
            HostType: null,
            CreatedAt: DateTimeOffset.MinValue,
            UpdatedAt: DateTimeOffset.MinValue,
            TurnCount: 0,
            Status: SessionStatus.Unknown,
            CopilotVersion: CopilotVersion.Zero,
            Locks: Array.Empty<SessionLockInfo>());
        return _githubLinkResolver.Resolve(shell);
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

    private async Task<SessionModelInfo?> TryReadModelInfoAsync(
        string sessionDir,
        bool hasStateDir,
        CopilotVersion version,
        CancellationToken cancellationToken)
    {
        if (!hasStateDir)
        {
            return null;
        }

        var eventsPath = Path.Combine(sessionDir, EventsFileName);
        if (!File.Exists(eventsPath))
        {
            return null;
        }

        var adapter = version == CopilotVersion.Zero
            ? _adapterRegistry.Latest
            : _adapterRegistry.Resolve(version).Adapter;

        try
        {
            await using var stream = new FileStream(
                eventsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var info = await adapter
                .ReadSessionModelInfoAsync(stream, cancellationToken)
                .ConfigureAwait(false);

            return info.CurrentModelId is null && info.UsageByModel.Count == 0
                ? null
                : info;
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not read model info from events.jsonl at {Path}.", eventsPath);
            return null;
        }
    }

    /// <summary>
    /// Reads <c>session.start.data.producer</c> from the first non-empty line
    /// of <c>events.jsonl</c> (#113). Producer is a stable, version-agnostic
    /// top-level field, so a tiny inline parser is preferred over going
    /// through the adapter pipeline. Returns <c>null</c> when the field is
    /// missing or unreadable; the chip group renders that as "(unknown)".
    /// </summary>
    private async Task<string?> TryReadProducerAsync(
        string sessionDir,
        bool hasStateDir,
        CancellationToken cancellationToken)
    {
        if (!hasStateDir)
        {
            return null;
        }

        var eventsPath = Path.Combine(sessionDir, EventsFileName);
        if (!File.Exists(eventsPath))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                eventsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            // Scan up to ~5 lines so we tolerate occasional preceding empty
            // / non-session.start events without paying the cost of streaming
            // the whole file.
            for (var i = 0; i < 5; i++)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    return null;
                }
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("data", out var data)
                        && data.ValueKind == System.Text.Json.JsonValueKind.Object
                        && data.TryGetProperty("producer", out var producer)
                        && producer.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var value = producer.GetString();
                        return string.IsNullOrWhiteSpace(value) ? null : value;
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // Tolerate malformed lines and try the next one.
                }
            }

            return null;
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not read producer from events.jsonl at {Path}.", eventsPath);
            return null;
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

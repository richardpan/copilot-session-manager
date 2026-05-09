using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotSessionManager.Core.GitHub.Issues;

/// <summary>
/// Default <see cref="IReadmeIssueRefProvider"/>. Delegates README I/O to
/// <see cref="ISessionReadmeStore"/> and the parsing to
/// <see cref="IssueRefScanner"/>. Forwards the store's
/// <see cref="ISessionReadmeStore.ReadmeChanged"/> event so view-models can
/// re-scan without taking a direct dependency on the store.
/// </summary>
public sealed class ReadmeIssueRefProvider : IReadmeIssueRefProvider, IDisposable
{
    private readonly ISessionReadmeStore _store;
    private readonly ILogger _logger;
    private bool _disposed;

    public ReadmeIssueRefProvider(
        ISessionReadmeStore store,
        ILogger<ReadmeIssueRefProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _store.ReadmeChanged += OnStoreReadmeChanged;
    }

    public event EventHandler<ReadmeIssueRefsChangedEventArgs>? ReadmeChanged;

    public async Task<IReadOnlyList<IssueRef>> GetParsedRefsAsync(
        string sessionId,
        string? defaultOwnerRepo,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        string? content;
        try
        {
            content = await _store.ReadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read README for {SessionId}; treating as no parsed refs.", sessionId);
            return Array.Empty<IssueRef>();
        }

        if (string.IsNullOrEmpty(content))
        {
            return Array.Empty<IssueRef>();
        }

        return IssueRefScanner.Scan(content, defaultOwnerRepo);
    }

    private void OnStoreReadmeChanged(object? sender, SessionReadmeChangedEventArgs e)
    {
        try
        {
            ReadmeChanged?.Invoke(this, new ReadmeIssueRefsChangedEventArgs(e.SessionId));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ReadmeChanged subscriber threw for {SessionId}.", e.SessionId);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _store.ReadmeChanged -= OnStoreReadmeChanged;
    }
}

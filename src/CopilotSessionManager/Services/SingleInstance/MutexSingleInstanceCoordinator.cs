using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Services.SingleInstance;

/// <summary>
/// Default <see cref="ISingleInstanceCoordinator"/> backed by a per-user
/// named <see cref="Mutex"/> for ownership and a per-user
/// <see cref="NamedPipeServerStream"/> for activation pings. Names are
/// scoped per Windows user and per Terminal Services session via the
/// <c>Local\</c> mutex namespace.
/// </summary>
public sealed class MutexSingleInstanceCoordinator : ISingleInstanceCoordinator
{
    private const string MutexPrefix = "Local\\CopilotSessionManager-";
    private const string PipePrefix = "CopilotSessionManager-";
    private const string PingMessage = "ACTIVATE";

    private readonly string _mutexName;
    private readonly string _pipeName;
    private readonly ILogger<MutexSingleInstanceCoordinator> _logger;
    private readonly CancellationTokenSource _listenerCts = new();

    private Mutex? _mutex;
    private bool _ownsMutex;
    private Task? _listenerTask;
    private int _disposed;

    public MutexSingleInstanceCoordinator(ILogger<MutexSingleInstanceCoordinator> logger)
        : this(logger, GetUserSuffix())
    {
    }

    /// <summary>
    /// Test seam — lets unit tests scope mutex/pipe names to a unique suffix
    /// per test run so parallel tests don't collide.
    /// </summary>
    public MutexSingleInstanceCoordinator(ILogger<MutexSingleInstanceCoordinator> logger, string instanceSuffix)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (string.IsNullOrWhiteSpace(instanceSuffix))
        {
            throw new ArgumentException("Instance suffix must be provided.", nameof(instanceSuffix));
        }

        _logger = logger;
        _mutexName = MutexPrefix + instanceSuffix;
        _pipeName = PipePrefix + instanceSuffix;
    }

    public event EventHandler? ActivationRequested;

    public async Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var mutex = new Mutex(initiallyOwned: false, _mutexName, out var createdNew);
        var owned = false;
        try
        {
            owned = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // Previous owner crashed without releasing — we still own it now.
            _logger.LogWarning("Detected abandoned single-instance mutex {MutexName}; taking ownership.", _mutexName);
            owned = true;
        }

        if (!owned)
        {
            mutex.Dispose();
            _logger.LogInformation("Another instance already running; pinging it via {PipeName}.", _pipeName);
            await SignalExistingInstanceAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        _mutex = mutex;
        _ownsMutex = true;
        _listenerTask = Task.Run(() => ListenAsync(_listenerCts.Token));
        _logger.LogInformation("Single-instance ownership acquired ({MutexName}, createdNew={CreatedNew}).", _mutexName, createdNew);
        return true;
    }

    private async Task SignalExistingInstanceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(connectCts.Token).ConfigureAwait(false);

            await using var writer = new StreamWriter(client) { AutoFlush = true };
            await writer.WriteLineAsync(PingMessage.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to ping existing instance via {PipeName}.", _pipeName);
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.Equals(line, PingMessage, StringComparison.Ordinal))
                {
                    _logger.LogInformation("Activation ping received on {PipeName}.", _pipeName);
                    try
                    {
                        ActivationRequested?.Invoke(this, EventArgs.Empty);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ActivationRequested handler threw.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Single-instance pipe listener error; continuing.");
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _listenerCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _listenerTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is AggregateException or OperationCanceledException)
        {
            // Listener cancellation is expected; nothing to log.
        }

        if (_mutex is not null)
        {
            try
            {
                if (_ownsMutex)
                {
                    _mutex.ReleaseMutex();
                }
            }
            catch (ApplicationException)
            {
                // Not owned by this thread — ignore.
            }
            _mutex.Dispose();
            _mutex = null;
        }

        _listenerCts.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed != 0)
        {
            throw new ObjectDisposedException(nameof(MutexSingleInstanceCoordinator));
        }
    }

    private static string GetUserSuffix()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value;
            if (!string.IsNullOrEmpty(sid))
            {
                return sid;
            }
        }
        catch
        {
            // Fall through to the username-based suffix.
        }

        return Environment.UserName;
    }
}

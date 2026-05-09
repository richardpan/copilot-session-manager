using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Services;

/// <summary>
/// <see cref="IFileLauncher"/> that delegates to <see cref="Process.Start(ProcessStartInfo)"/>
/// with <c>UseShellExecute = true</c>, asking the OS to open the path with
/// its registered default handler.
/// </summary>
public sealed class ShellFileLauncher : IFileLauncher
{
    private readonly ILogger<ShellFileLauncher> _logger;

    public ShellFileLauncher(ILogger<ShellFileLauncher> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public Task OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        };

        try
        {
            using var _ = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open {Path} with shell handler.", path);
            throw;
        }

        return Task.CompletedTask;
    }
}

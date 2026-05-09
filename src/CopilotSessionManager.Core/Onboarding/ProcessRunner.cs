using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Onboarding;

/// <summary>
/// Default <see cref="IProcessRunner"/> backed by <see cref="Process"/>.
/// Captures stdout + stderr to in-memory buffers and enforces a hard timeout.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private readonly ILogger<ProcessRunner> _logger;

    public ProcessRunner(ILogger<ProcessRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var psi = new ProcessStartInfo
        {
            FileName = request.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in request.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger.LogDebug(ex, "Could not start {FileName}.", request.FileName);
            return ProcessRunResult.NotFound;
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogDebug(ex, "Could not start {FileName}.", request.FileName);
            return ProcessRunResult.NotFound;
        }

        if (process is null)
        {
            return ProcessRunResult.NotFound;
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Process {FileName} timed out after {Seconds}s; killing.", request.FileName, request.TimeoutSeconds);
            try
            { process.Kill(entireProcessTree: true); }
            catch (Exception killEx) { _logger.LogDebug(killEx, "Kill failed."); }
            return new ProcessRunResult(ExitCode: -2, StdOut: stdout.ToString(), StdErr: $"timed out after {request.TimeoutSeconds}s");
        }

        return new ProcessRunResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}

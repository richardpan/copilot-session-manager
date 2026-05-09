using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Onboarding;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Cli.Share;

/// <summary>
/// Default <see cref="ICopilotShareInvoker"/>. Delegates process execution
/// to the shared <see cref="IProcessRunner"/> abstraction so it can be unit
/// tested without spawning real <c>copilot</c> processes.
/// </summary>
/// <remarks>
/// Failure classification is intentionally inlined (rather than reused from
/// <c>GhCliResultClassifier</c>) because the surface is much smaller — the
/// only "expected" failure modes are: CLI missing, non-zero exit, timeout,
/// and the share file not being produced. The classifier is kept private so
/// future tweaks don't ripple into other CLI invokers.
/// </remarks>
public sealed class CopilotShareInvoker : ICopilotShareInvoker
{
    /// <summary>Default executable; resolved via PATH like every other CLI shim.</summary>
    public const string DefaultExecutable = "copilot";

    /// <summary>Hard ceiling on a single share invocation.</summary>
    public const int DefaultTimeoutSeconds = 30;

    private readonly IProcessRunner _runner;
    private readonly ILogger<CopilotShareInvoker> _logger;
    private readonly string _executable;
    private readonly int _timeoutSeconds;
    private readonly Func<string> _tempFileFactory;

    public CopilotShareInvoker(IProcessRunner runner, ILogger<CopilotShareInvoker> logger)
        : this(runner, logger, DefaultExecutable, DefaultTimeoutSeconds, tempFileFactory: null)
    {
    }

    /// <summary>
    /// Test-friendly constructor. <paramref name="tempFileFactory"/> may be
    /// supplied to point exports at a deterministic location.
    /// </summary>
    public CopilotShareInvoker(
        IProcessRunner runner,
        ILogger<CopilotShareInvoker> logger,
        string executable,
        int timeoutSeconds,
        Func<string>? tempFileFactory)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (timeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Must be positive.");
        }

        _runner = runner;
        _logger = logger;
        _executable = executable;
        _timeoutSeconds = timeoutSeconds;
        _tempFileFactory = tempFileFactory ?? CreateDefaultTempFile;
    }

    public async Task<ShareResult> ExportAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return ShareResult.Fail("Session id is required.");
        }

        string tempPath;
        try
        {
            tempPath = _tempFileFactory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not create a temp file for copilot --share output.");
            return ShareResult.Fail("Could not create a temp file to receive the shared transcript.");
        }

        var args = new[]
        {
            "--resume",
            sessionId,
            $"--share={tempPath}",
        };

        _logger.LogInformation(
            "Invoking copilot --share for session {SessionId} → {TempPath}.",
            sessionId,
            tempPath);

        ProcessRunResult run;
        try
        {
            run = await _runner
                .RunAsync(new ProcessRunRequest(_executable, args, TimeoutSeconds: _timeoutSeconds), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDeleteTemp(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error invoking copilot --share for session {SessionId}.", sessionId);
            TryDeleteTemp(tempPath);
            return ShareResult.Fail($"Unexpected error invoking copilot CLI: {ex.Message}");
        }

        if (run == ProcessRunResult.NotFound)
        {
            _logger.LogWarning("copilot CLI not found on PATH; cannot export session {SessionId}.", sessionId);
            TryDeleteTemp(tempPath);
            return ShareResult.Fail("copilot CLI not found on PATH. Install it to enable session merge.");
        }

        if (run.ExitCode == -2)
        {
            _logger.LogWarning(
                "copilot --share for session {SessionId} timed out after {Seconds}s.",
                sessionId,
                _timeoutSeconds);
            TryDeleteTemp(tempPath);
            return ShareResult.Fail($"copilot --share timed out after {_timeoutSeconds} seconds.");
        }

        if (!run.Success)
        {
            var detail = string.IsNullOrWhiteSpace(run.StdErr) ? run.StdOut : run.StdErr;
            _logger.LogWarning(
                "copilot --share for session {SessionId} exited {Exit}: {Detail}",
                sessionId,
                run.ExitCode,
                detail.Trim());
            TryDeleteTemp(tempPath);
            return ShareResult.Fail($"copilot --share exited {run.ExitCode}: {Truncate(detail.Trim(), 200)}");
        }

        if (!File.Exists(tempPath))
        {
            _logger.LogWarning(
                "copilot --share for session {SessionId} reported success but produced no output file at {Path}.",
                sessionId,
                tempPath);
            return ShareResult.Fail("copilot --share reported success but produced no output file.");
        }

        string markdown;
        try
        {
            markdown = await File.ReadAllTextAsync(tempPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read shared transcript at {Path}.", tempPath);
            TryDeleteTemp(tempPath);
            return ShareResult.Fail($"Could not read shared transcript: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(markdown))
        {
            _logger.LogWarning("Shared transcript at {Path} was empty for session {SessionId}.", tempPath, sessionId);
            TryDeleteTemp(tempPath);
            return ShareResult.Fail("copilot --share produced an empty transcript.");
        }

        return ShareResult.Ok(tempPath, markdown);
    }

    private static string CreateDefaultTempFile()
    {
        // GetTempFileName returns a 0-byte .tmp file; rename to .md so the
        // CLI's heuristics (and any user inspection) treat it as markdown.
        var tmp = Path.GetTempFileName();
        var mdPath = Path.ChangeExtension(tmp, ".md");
        try
        {
            File.Move(tmp, mdPath, overwrite: true);
        }
        catch
        {
            // If the rename fails for any reason fall back to the .tmp path
            // so we still have a unique writable file.
            return tmp;
        }
        return mdPath;
    }

    private static void TryDeleteTemp(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort; nothing else to do.
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

using System.Diagnostics;
using CopilotSessionManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.GitHub;

/// <summary>
/// <see cref="IGitHubClient"/> that shells out to the <c>gh</c> CLI. Falls
/// back to <c>null</c> for every "expected" failure (gh missing, no PR,
/// non-zero exit, parse failure) so callers don't need try/catch.
/// </summary>
public sealed class GhCliGitHubClient : IGitHubClient
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly ILogger<GhCliGitHubClient> _logger;
    private readonly string _ghExecutable;
    private readonly TimeSpan _timeout;

    public GhCliGitHubClient(ILogger<GhCliGitHubClient> logger)
        : this(logger, ghExecutable: "gh", timeout: DefaultTimeout)
    {
    }

    public GhCliGitHubClient(ILogger<GhCliGitHubClient> logger, string ghExecutable, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(ghExecutable);

        _logger = logger;
        _ghExecutable = ghExecutable;
        _timeout = timeout > TimeSpan.Zero ? timeout : DefaultTimeout;
    }

    public async Task<PullRequestInfo?> FindPullRequestAsync(
        string repositorySlug,
        string headBranch,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositorySlug) || string.IsNullOrWhiteSpace(headBranch))
        {
            return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = _ghExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("pr");
        psi.ArgumentList.Add("list");
        psi.ArgumentList.Add("--repo");
        psi.ArgumentList.Add(repositorySlug);
        psi.ArgumentList.Add("--head");
        psi.ArgumentList.Add(headBranch);
        psi.ArgumentList.Add("--state");
        psi.ArgumentList.Add("all");
        psi.ArgumentList.Add("--limit");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("--json");
        psi.ArgumentList.Add("number,title,state,isDraft,url");

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to launch gh ({Exe}); skipping PR lookup.", _ghExecutable);
            return null;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                _logger.LogDebug("gh pr list timed out after {Timeout} for {Repo}#{Branch}.",
                    _timeout, repositorySlug, headBranch);
                return null;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                _logger.LogDebug(
                    "gh pr list exited {Exit} for {Repo}#{Branch}: {Stderr}",
                    process.ExitCode, repositorySlug, headBranch, stderr.Trim());
                return null;
            }

            return GhPullRequestJsonParser.ParseFirst(stdout);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unexpected error running gh pr list for {Repo}#{Branch}.",
                repositorySlug, headBranch);
            return null;
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best-effort
        }
    }
}

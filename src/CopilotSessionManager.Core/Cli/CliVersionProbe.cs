using CopilotSessionManager.Core.Onboarding;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Cli;

public sealed class CliVersionProbe : ICliVersionProbe
{
    public const int DefaultTimeoutSeconds = 5;

    private readonly IProcessRunner _runner;
    private readonly MinimumSupportedVersions _minimums;
    private readonly ILogger<CliVersionProbe> _logger;

    public CliVersionProbe(IProcessRunner runner, ILogger<CliVersionProbe> logger)
        : this(runner, MinimumSupportedVersions.Default, logger)
    {
    }

    public CliVersionProbe(
        IProcessRunner runner,
        MinimumSupportedVersions minimums,
        ILogger<CliVersionProbe> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(minimums);
        ArgumentNullException.ThrowIfNull(logger);

        _runner = runner;
        _minimums = minimums;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CliVersionInfo>> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var gh = await ProbeOneAsync("gh", "gh", _minimums.Gh, cancellationToken).ConfigureAwait(false);
        var copilot = await ProbeOneAsync("copilot", "copilot", _minimums.Copilot, cancellationToken).ConfigureAwait(false);
        return new[] { gh, copilot };
    }

    private async Task<CliVersionInfo> ProbeOneAsync(
        string cli,
        string executable,
        Version minimum,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _runner.RunAsync(
                new ProcessRunRequest(executable, new[] { "--version" }, TimeoutSeconds: DefaultTimeoutSeconds),
                cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                var rawFailure = FirstNonEmptyLine(result.StdErr) ?? FirstNonEmptyLine(result.StdOut) ?? result.StdErr.Trim();
                if (string.IsNullOrWhiteSpace(rawFailure))
                {
                    rawFailure = result == ProcessRunResult.NotFound
                        ? "executable not found"
                        : $"exit {result.ExitCode}";
                }

                return Unavailable(cli, minimum, rawFailure);
            }

            var output = string.IsNullOrWhiteSpace(result.StdOut) ? result.StdErr : result.StdOut;
            if (!CliVersionParser.TryParse(output, out var version))
            {
                var rawUnparseable = FirstNonEmptyLine(output) ?? "unparseable version output";
                return Unavailable(cli, minimum, rawUnparseable);
            }

            var rawLine = FirstVersionLine(output) ?? FirstNonEmptyLine(output) ?? version.ToString();
            return new CliVersionInfo(cli, version, minimum, version < minimum, rawLine.Trim());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to probe {Cli} CLI version.", cli);
            return Unavailable(cli, minimum, ex.Message);
        }
    }

    private static CliVersionInfo Unavailable(string cli, Version minimum, string rawVersionLine) =>
        new(cli, new Version(0, 0, 0), minimum, IsOutdated: true, rawVersionLine.Trim());

    private static string? FirstVersionLine(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        foreach (var line in output.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
        {
            if (CliVersionParser.TryParse(line, out _))
            {
                return line;
            }
        }

        return null;
    }

    private static string? FirstNonEmptyLine(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        return output
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line));
    }
}

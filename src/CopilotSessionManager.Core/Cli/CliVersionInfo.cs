namespace CopilotSessionManager.Core.Cli;

public sealed record CliVersionInfo(
    string Cli,
    Version Detected,
    Version Minimum,
    bool IsOutdated,
    string RawVersionLine);

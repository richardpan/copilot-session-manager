namespace CopilotSessionManager.Core.Cli;

public sealed record MinimumSupportedVersions(Version Gh, Version Copilot)
{
    public static MinimumSupportedVersions Default { get; } = new(
        Gh: new Version(2, 40, 0),
        Copilot: new Version(1, 0, 0));
}

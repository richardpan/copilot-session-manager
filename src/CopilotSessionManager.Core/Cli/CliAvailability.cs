namespace CopilotSessionManager.Core.Cli;

public enum CliAvailability
{
    Available,
    Outdated,
    NotInstalled,
}

public sealed record CliAvailabilityState(
    CliAvailability State,
    IReadOnlyList<CliVersionInfo> Probes,
    string? UserMessage,
    DateTimeOffset DetectedAt)
{
    public static CliAvailabilityState InitialAvailable(DateTimeOffset detectedAt) =>
        new(CliAvailability.Available, Array.Empty<CliVersionInfo>(), null, detectedAt);
}

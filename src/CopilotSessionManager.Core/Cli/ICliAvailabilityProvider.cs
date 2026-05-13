namespace CopilotSessionManager.Core.Cli;

public interface ICliAvailabilityProvider
{
    CliAvailabilityState Current { get; }

    event EventHandler<CliAvailabilityState>? AvailabilityChanged;

    void Report(
        CliAvailability state,
        IReadOnlyList<CliVersionInfo>? probes = null,
        string? userMessage = null);
}

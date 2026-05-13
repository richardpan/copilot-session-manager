namespace CopilotSessionManager.Core.Cli;

public interface ICliVersionProbe
{
    Task<IReadOnlyList<CliVersionInfo>> ProbeAsync(CancellationToken cancellationToken = default);
}

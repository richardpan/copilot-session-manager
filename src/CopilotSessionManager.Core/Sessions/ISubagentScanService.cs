using CopilotSessionManager.Core.Models;

namespace CopilotSessionManager.Core.Sessions;

public interface ISubagentScanService
{
    Task<IReadOnlyList<SubagentSummary>> ScanAsync(string sessionId, CancellationToken ct = default);
}

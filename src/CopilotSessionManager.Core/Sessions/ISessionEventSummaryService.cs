using CopilotSessionManager.Core.Models;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// Streams a session's <c>events.jsonl</c> file and returns an aggregated
/// <see cref="SessionEventSummary"/> the README renderer can consume to fill
/// in the auto-generated activity sections. Implementations must:
/// <list type="bullet">
///   <item>Tolerate missing files / malformed lines (return
///   <see cref="SessionEventSummary.Empty"/> on missing file).</item>
///   <item>Stream the file rather than read it whole — events.jsonl can grow
///   to many megabytes for long-running sessions.</item>
/// </list>
/// </summary>
public interface ISessionEventSummaryService
{
    Task<SessionEventSummary> ScanAsync(string sessionId, CancellationToken ct = default);
}

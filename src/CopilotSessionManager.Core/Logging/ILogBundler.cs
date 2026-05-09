using System.Threading;
using System.Threading.Tasks;

namespace CopilotSessionManager.Core.Logging;

/// <summary>
/// Bundles the application's rolling log files into a single zip the user can
/// attach to a bug report. Implementations must NOT re-redact: logs on disk
/// have already been scrubbed at write time by the Serilog enricher.
/// </summary>
public interface ILogBundler
{
    /// <summary>
    /// Bundle every <c>*.log</c> file in the application's logs directory into
    /// a zip file at <paramref name="destinationPath"/>. The zip also contains
    /// a <c>manifest.txt</c> with app version, OS, and the bundle timestamp.
    /// </summary>
    /// <param name="destinationPath">Absolute target path (must end with
    /// <c>.zip</c>; the parent directory will be created if missing).</param>
    /// <param name="cancellationToken">Cancellation propagated to file IO.</param>
    Task<LogBundleResult> BundleAsync(string destinationPath, CancellationToken cancellationToken = default);
}

using System.Threading;
using System.Threading.Tasks;

namespace CopilotSessionManager.Services;

/// <summary>
/// Opens a file or URL using the OS default handler. Abstracted so view
/// model tests don't actually launch external applications.
/// </summary>
public interface IFileLauncher
{
    /// <summary>
    /// Opens <paramref name="path"/> with its system default application.
    /// Returns once the launch has been requested (does not wait for the
    /// child process to exit).
    /// </summary>
    Task OpenAsync(string path, CancellationToken cancellationToken = default);
}

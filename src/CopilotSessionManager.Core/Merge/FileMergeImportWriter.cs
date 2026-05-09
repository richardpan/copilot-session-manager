using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Merge;

/// <summary>
/// Default <see cref="IMergeImportWriter"/>. Writes each merge into
/// <c>&lt;session&gt;/merge-imports/&lt;timestamp&gt;-from-&lt;sourceId&gt;.md</c>
/// inside the target session folder.
/// </summary>
public sealed class FileMergeImportWriter : IMergeImportWriter
{
    /// <summary>Sub-folder under the target session that receives merge files.</summary>
    public const string ImportsFolderName = "merge-imports";

    private readonly ISessionFolderReader _folders;
    private readonly TimeProvider _clock;
    private readonly ILogger<FileMergeImportWriter> _logger;

    public FileMergeImportWriter(
        ISessionFolderReader folders,
        TimeProvider clock,
        ILogger<FileMergeImportWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _folders = folders;
        _clock = clock;
        _logger = logger;
    }

    public async Task<string> WriteAsync(
        string targetSessionId,
        string sourceSessionId,
        string markdown,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSessionId);
        ArgumentNullException.ThrowIfNull(markdown);

        var targetFolder = _folders.GetSessionFolderPath(targetSessionId);
        var importsFolder = Path.Combine(targetFolder, ImportsFolderName);

        try
        {
            Directory.CreateDirectory(importsFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(
                ex,
                "Could not create merge-imports folder at {Folder}.",
                importsFolder);
            throw;
        }

        var timestamp = _clock.GetUtcNow().ToString("yyyyMMddTHHmmssZ", System.Globalization.CultureInfo.InvariantCulture);
        var safeSource = MakeFilenameSafe(sourceSessionId);
        var path = Path.Combine(importsFolder, $"{timestamp}-from-{safeSource}.md");

        var temp = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, markdown, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDeleteTemp(temp);
            throw;
        }

        _logger.LogInformation(
            "Wrote merge import for source {SourceId} into target {TargetId} at {Path}.",
            sourceSessionId,
            targetSessionId,
            path);

        return path;
    }

    private static string MakeFilenameSafe(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }
        return sb.ToString();
    }

    private static void TryDeleteTemp(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup; nothing else to do.
        }
    }
}

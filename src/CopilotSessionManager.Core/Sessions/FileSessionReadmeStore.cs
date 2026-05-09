using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// Stores <c>SESSION-README.md</c> at
/// <c>&lt;sessionFolder&gt;/SESSION-README.md</c>. Preserves user-editable
/// blocks delimited by <c>USER:BEGIN &lt;name&gt;</c> / <c>USER:END &lt;name&gt;</c>
/// HTML comments across regenerations.
/// </summary>
public sealed class FileSessionReadmeStore : ISessionReadmeStore
{
    /// <summary>The on-disk file name for every README.</summary>
    public const string FileName = "SESSION-README.md";

    private readonly ISessionFolderReader _folders;
    private readonly ILogger<FileSessionReadmeStore> _logger;

    public FileSessionReadmeStore(
        ISessionFolderReader folders,
        ILogger<FileSessionReadmeStore> logger)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(logger);
        _folders = folders;
        _logger = logger;
    }

    public event EventHandler<SessionReadmeChangedEventArgs>? ReadmeChanged;

    public string GetReadmePath(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Path.Combine(_folders.GetSessionFolderPath(sessionId), FileName);
    }

    public bool Exists(string sessionId) => File.Exists(GetReadmePath(sessionId));

    public async Task<string?> ReadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var path = GetReadmePath(sessionId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read README at {Path}; treating as missing.", path);
            return null;
        }
    }

    public async Task<string> WriteAsync(
        string sessionId,
        string freshlyRendered,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(freshlyRendered);

        var path = GetReadmePath(sessionId);
        var folder = Path.GetDirectoryName(path)!;

        var existing = await ReadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var merged = existing is null ? freshlyRendered : MergeUserBlocks(freshlyRendered, existing);

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not create folder {Folder} for README.", folder);
            throw;
        }

        var temp = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, merged, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDeleteTemp(temp);
            throw;
        }

        ReadmeChanged?.Invoke(this, new SessionReadmeChangedEventArgs(sessionId, path));
        return merged;
    }

    /// <summary>
    /// Replaces the body of each user block in <paramref name="freshlyRendered"/>
    /// with the corresponding body in <paramref name="existing"/> when one is
    /// present and well-formed. Unmatched blocks pass through unchanged.
    /// </summary>
    internal static string MergeUserBlocks(string freshlyRendered, string existing)
    {
        var existingBlocks = ExtractUserBlocks(existing);
        if (existingBlocks.Count == 0)
        {
            return freshlyRendered;
        }

        var result = new StringBuilder(freshlyRendered.Length + 256);
        var cursor = 0;
        while (cursor < freshlyRendered.Length)
        {
            var beginIdx = freshlyRendered.IndexOf(
                TemplatedSessionReadmeRenderer.UserBeginPrefix, cursor, StringComparison.Ordinal);
            if (beginIdx < 0)
            {
                result.Append(freshlyRendered, cursor, freshlyRendered.Length - cursor);
                break;
            }

            var nameStart = beginIdx + TemplatedSessionReadmeRenderer.UserBeginPrefix.Length;
            var suffixIdx = freshlyRendered.IndexOf(
                TemplatedSessionReadmeRenderer.MarkerSuffix, nameStart, StringComparison.Ordinal);
            if (suffixIdx < 0)
            {
                result.Append(freshlyRendered, cursor, freshlyRendered.Length - cursor);
                break;
            }

            var name = freshlyRendered.Substring(nameStart, suffixIdx - nameStart).Trim();
            var openLineEnd = NextLineEnd(freshlyRendered, suffixIdx);
            var endMarker = TemplatedSessionReadmeRenderer.UserEndPrefix + name + TemplatedSessionReadmeRenderer.MarkerSuffix;
            var endIdx = freshlyRendered.IndexOf(endMarker, openLineEnd, StringComparison.Ordinal);
            if (endIdx < 0)
            {
                result.Append(freshlyRendered, cursor, freshlyRendered.Length - cursor);
                break;
            }

            // Copy through the opening marker line (inclusive of trailing newline).
            result.Append(freshlyRendered, cursor, openLineEnd - cursor);

            if (existingBlocks.TryGetValue(name, out var preserved))
            {
                result.Append(preserved);
                if (preserved.Length > 0 && preserved[^1] != '\n')
                {
                    result.Append('\n');
                }
            }
            else
            {
                result.Append(freshlyRendered, openLineEnd, endIdx - openLineEnd);
            }

            // Continue right at the closing marker — let the loop emit it next iteration.
            cursor = endIdx;
        }

        return result.ToString();
    }

    private static IReadOnlyDictionary<string, string> ExtractUserBlocks(string content)
    {
        var blocks = new Dictionary<string, string>(StringComparer.Ordinal);
        var cursor = 0;
        while (cursor < content.Length)
        {
            var beginIdx = content.IndexOf(
                TemplatedSessionReadmeRenderer.UserBeginPrefix, cursor, StringComparison.Ordinal);
            if (beginIdx < 0)
            {
                break;
            }

            var nameStart = beginIdx + TemplatedSessionReadmeRenderer.UserBeginPrefix.Length;
            var suffixIdx = content.IndexOf(
                TemplatedSessionReadmeRenderer.MarkerSuffix, nameStart, StringComparison.Ordinal);
            if (suffixIdx < 0)
            {
                break;
            }

            var name = content.Substring(nameStart, suffixIdx - nameStart).Trim();
            var bodyStart = NextLineEnd(content, suffixIdx);
            var endMarker = TemplatedSessionReadmeRenderer.UserEndPrefix + name + TemplatedSessionReadmeRenderer.MarkerSuffix;
            var endIdx = content.IndexOf(endMarker, bodyStart, StringComparison.Ordinal);
            if (endIdx < 0)
            {
                cursor = bodyStart;
                continue;
            }

            blocks[name] = content.Substring(bodyStart, endIdx - bodyStart);
            cursor = endIdx + endMarker.Length;
        }

        return blocks;
    }

    private static int NextLineEnd(string s, int from)
    {
        var idx = s.IndexOf('\n', from);
        return idx < 0 ? s.Length : idx + 1;
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

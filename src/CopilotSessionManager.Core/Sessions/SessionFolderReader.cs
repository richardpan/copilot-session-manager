using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// File-system implementation of <see cref="ISessionFolderReader"/>. Reads
/// from <c>&lt;sessionStateDir&gt;/&lt;id&gt;/</c>.
/// </summary>
public sealed class SessionFolderReader : ISessionFolderReader
{
    private static readonly Regex CheckpointFileRegex = new(
        @"^(\d{1,4})[-_].+\.md$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ICopilotPaths _paths;
    private readonly ILogger<SessionFolderReader> _logger;

    public SessionFolderReader(ICopilotPaths paths, ILogger<SessionFolderReader> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        _paths = paths;
        _logger = logger;
    }

    public string GetSessionFolderPath(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Path.Combine(_paths.SessionStateDirectory, sessionId);
    }

    public async Task<IReadOnlyList<SessionCheckpointSummary>> GetCheckpointsAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var folder = Path.Combine(GetSessionFolderPath(sessionId), "checkpoints");
        if (!Directory.Exists(folder))
        {
            return Array.Empty<SessionCheckpointSummary>();
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(folder, "*.md", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not enumerate checkpoint folder {Folder}.", folder);
            return Array.Empty<SessionCheckpointSummary>();
        }

        var results = new List<SessionCheckpointSummary>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(file);
            var match = CheckpointFileRegex.Match(name);
            if (!match.Success)
            {
                continue;
            }

            if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                continue;
            }

            string? title = await ReadFirstHeadingAsync(file, cancellationToken).ConfigureAwait(false);
            results.Add(new SessionCheckpointSummary(
                Number: number,
                Title: string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(name) : title!,
                FilePath: file));
        }

        return results.OrderBy(c => c.Number).ToList();
    }

    private async Task<string?> ReadFirstHeadingAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096, useAsync: true);
            using var reader = new StreamReader(stream);
            for (var i = 0; i < 50; i++)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                var trimmed = line.TrimStart();
                if (trimmed.StartsWith('#'))
                {
                    return trimmed.TrimStart('#').Trim();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read checkpoint heading from {Path}.", path);
        }

        return null;
    }
}

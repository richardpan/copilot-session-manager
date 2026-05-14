using System;
using System.IO;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// V1.3 (#147): Default <see cref="IDocFreshnessService"/>. Inspects the
/// modification time of <c>SESSION-README.md</c> and (if present)
/// <c>SESSION-DOCS.md</c> in the session folder and maps it to a
/// <see cref="DocFreshnessState"/> using fixed thresholds.
/// </summary>
public sealed class DocFreshnessService : IDocFreshnessService
{
    /// <summary>Sessions younger than this are reported as <see cref="DocFreshnessState.NotApplicable"/>.</summary>
    public static readonly TimeSpan MinSessionAge = TimeSpan.FromMinutes(30);

    /// <summary>Doc files newer than this are <see cref="DocFreshnessState.Fresh"/>.</summary>
    public static readonly TimeSpan FreshThreshold = TimeSpan.FromDays(1);

    /// <summary>Doc files older than this are <see cref="DocFreshnessState.VeryStale"/>.</summary>
    public static readonly TimeSpan VeryStaleThreshold = TimeSpan.FromDays(7);

    private readonly ISessionFolderReader _folders;
    private readonly TimeProvider _timeProvider;

    public DocFreshnessService(ISessionFolderReader folders, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _folders = folders;
        _timeProvider = timeProvider;
    }

    public DocFreshnessResult Evaluate(string sessionId, DateTimeOffset sessionCreatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var now = _timeProvider.GetUtcNow();

        if (now - sessionCreatedAt < MinSessionAge)
        {
            return new DocFreshnessResult(DocFreshnessState.NotApplicable, null);
        }

        DateTimeOffset? newest = null;
        try
        {
            var folder = _folders.GetSessionFolderPath(sessionId);
            newest = NewestMtime(folder, FileSessionReadmeStore.FileName, SessionDocsService.DocsMarkdownFileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Treat IO trouble the same as missing — the badge is best-effort.
            newest = null;
        }

        if (newest is null)
        {
            return new DocFreshnessResult(DocFreshnessState.Missing, null);
        }

        var age = now - newest.Value;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age <= FreshThreshold)
        {
            return new DocFreshnessResult(DocFreshnessState.Fresh, null);
        }

        var ageDays = (int)Math.Floor(age.TotalDays);
        return age > VeryStaleThreshold
            ? new DocFreshnessResult(DocFreshnessState.VeryStale, ageDays)
            : new DocFreshnessResult(DocFreshnessState.Stale, ageDays);
    }

    private static DateTimeOffset? NewestMtime(string folder, params string[] fileNames)
    {
        DateTimeOffset? newest = null;
        foreach (var name in fileNames)
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path))
            {
                continue;
            }

            DateTimeOffset mtime;
            try
            {
                mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (newest is null || mtime > newest.Value)
            {
                newest = mtime;
            }
        }
        return newest;
    }
}

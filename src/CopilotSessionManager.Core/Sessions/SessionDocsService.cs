using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;
using Markdig;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// V1.6 (#118): Default <see cref="ISessionDocsService"/>. Scaffolds
/// <c>SESSION-DOCS.md</c> on first sight and generates a self-contained
/// <c>SESSION-DOCS.html</c> on demand from the markdown plus auto-derived
/// indexes of mockups, files, plan and checkpoints.
/// </summary>
public sealed class SessionDocsService : ISessionDocsService
{
    /// <summary>The on-disk file name for the user/agent-curated markdown source.</summary>
    public const string DocsMarkdownFileName = "SESSION-DOCS.md";

    /// <summary>The on-disk file name for the rendered HTML view.</summary>
    public const string DocsHtmlFileName = "SESSION-DOCS.html";

    /// <summary>Files larger than this are listed but not embedded or used for stale checks.</summary>
    public const long MaxRenderableFileBytes = 5L * 1024 * 1024;

    private static readonly HashSet<string> ExcludedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "events.jsonl",
        "session.db",
        "session.db-wal",
        "session.db-shm",
        "vscode.metadata.json",
        "workspace.yaml",
        DocsHtmlFileName,
    };

    private static readonly HashSet<string> ExcludedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "rewind-snapshots",
        ".git",
        "obj",
        "bin",
    };

    private readonly ISessionFolderReader _folders;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SessionDocsService> _logger;
    private readonly MarkdownPipeline _markdownPipeline;

    public SessionDocsService(
        ISessionFolderReader folders,
        TimeProvider timeProvider,
        ILogger<SessionDocsService> logger)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _folders = folders;
        _timeProvider = timeProvider;
        _logger = logger;

        _markdownPipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseSoftlineBreakAsHardlineBreak()
            .Build();
    }

    public string GetDocsMarkdownPath(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Path.Combine(_folders.GetSessionFolderPath(sessionId), DocsMarkdownFileName);
    }

    public string GetDocsHtmlPath(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Path.Combine(_folders.GetSessionFolderPath(sessionId), DocsHtmlFileName);
    }

    public async Task<string> EnsureAsync(Session session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var folder = _folders.GetSessionFolderPath(session.Id);
        var mdPath = Path.Combine(folder, DocsMarkdownFileName);
        var htmlPath = Path.Combine(folder, DocsHtmlFileName);

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not create session folder {Folder} for docs.", folder);
            throw;
        }

        // Step 1: Scaffold the markdown if (and only if) it doesn't exist.
        // We never overwrite — the user/agent owns this file once created.
        if (!File.Exists(mdPath))
        {
            await ScaffoldMarkdownAsync(mdPath, session, cancellationToken).ConfigureAwait(false);
        }

        // Step 2: Stale check — regenerate the HTML if it's missing or any
        // source under the session folder is newer than the rendered .html.
        if (NeedsRegeneration(folder, htmlPath))
        {
            await GenerateHtmlAsync(folder, htmlPath, session, cancellationToken).ConfigureAwait(false);
        }

        return htmlPath;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Markdown scaffolding
    // ─────────────────────────────────────────────────────────────────────

    private async Task ScaffoldMarkdownAsync(
        string mdPath,
        Session session,
        CancellationToken cancellationToken)
    {
        var displayName = !string.IsNullOrWhiteSpace(session.Summary)
            ? session.Summary!
            : session.Id;

        var seed = BuildSeedMarkdown(displayName);
        var temp = mdPath + ".tmp";

        try
        {
            await File.WriteAllTextAsync(temp, seed, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temp, mdPath, overwrite: false);
            _logger.LogInformation("Scaffolded {File} for session {Id}.", DocsMarkdownFileName, session.Id);
        }
        catch (IOException) when (File.Exists(mdPath))
        {
            // Concurrent scaffold — somebody else won the race. Acceptable.
            TryDeleteTemp(temp);
        }
        catch
        {
            TryDeleteTemp(temp);
            throw;
        }
    }

    private static string BuildSeedMarkdown(string displayName)
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine("<!--");
        sb.AppendLine("    This file is managed by Copilot Session Manager (csm).");
        sb.AppendLine("    Edit freely — csm will NEVER overwrite your changes.");
        sb.AppendLine("    csm renders this file (plus auto-derived mockup, file, plan and");
        sb.AppendLine("    checkpoint indexes) into SESSION-DOCS.html for in-browser viewing.");
        sb.AppendLine();
        sb.AppendLine("    When the user asks to \"update documentation\" for this session,");
        sb.AppendLine("    edit this file.");
        sb.AppendLine("-->");
        sb.AppendLine();
        sb.Append("# ").AppendLine(displayName);
        sb.AppendLine();
        sb.AppendLine("## Overview");
        sb.AppendLine();
        sb.AppendLine("*Brief description of what this session is about.*");
        sb.AppendLine();
        sb.AppendLine("## Decisions");
        sb.AppendLine();
        sb.AppendLine("*Decisions made together with the user. Include rationale.*");
        sb.AppendLine();
        sb.AppendLine("## Features");
        sb.AppendLine();
        sb.AppendLine("*Agreed feature list for this session.*");
        sb.AppendLine();
        sb.AppendLine("## Expected behavior");
        sb.AppendLine();
        sb.AppendLine("*How the feature should behave from the user's perspective.*");
        sb.AppendLine();
        sb.AppendLine("## Mockups");
        sb.AppendLine();
        sb.AppendLine("*Links to any mockups, diagrams, or visual artifacts (e.g. files/foo.html).*");
        sb.AppendLine();
        sb.AppendLine("## Notes");
        sb.AppendLine();
        sb.AppendLine("*Anything else worth capturing.*");
        sb.AppendLine();
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Stale check
    // ─────────────────────────────────────────────────────────────────────

    private bool NeedsRegeneration(string folder, string htmlPath)
    {
        if (!File.Exists(htmlPath))
        {
            return true;
        }

        DateTime htmlMtime;
        try
        {
            htmlMtime = File.GetLastWriteTimeUtc(htmlPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read mtime of {Html}; assuming stale.", htmlPath);
            return true;
        }

        foreach (var source in EnumerateSourceFiles(folder))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(source) > htmlMtime)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not stat {Source}; treating as stale.", source);
                return true;
            }
        }

        return false;
    }

    private IEnumerable<string> EnumerateSourceFiles(string folder)
    {
        // SESSION-DOCS.md (root).
        var docsMd = Path.Combine(folder, DocsMarkdownFileName);
        if (File.Exists(docsMd))
        {
            yield return docsMd;
        }

        // plan.md (root).
        var planMd = Path.Combine(folder, "plan.md");
        if (File.Exists(planMd))
        {
            yield return planMd;
        }

        // files/, research/, checkpoints/.
        foreach (var sub in new[] { "files", "research", "checkpoints" })
        {
            var subDir = Path.Combine(folder, sub);
            if (!Directory.Exists(subDir))
            {
                continue;
            }

            string[] entries;
            try
            {
                entries = Directory.GetFiles(subDir, "*", SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not enumerate {SubDir}.", subDir);
                continue;
            }

            foreach (var entry in entries)
            {
                if (ShouldSkipFile(entry, folder))
                {
                    continue;
                }

                yield return entry;
            }
        }
    }

    private static bool ShouldSkipFile(string fullPath, string folder)
    {
        var name = Path.GetFileName(fullPath);
        if (ExcludedFileNames.Contains(name))
        {
            return true;
        }

        // inuse.*.lock pattern.
        if (name.StartsWith("inuse.", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Walk up parents looking for excluded folder names.
        var dir = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(dir) &&
               !string.Equals(dir, folder, StringComparison.OrdinalIgnoreCase))
        {
            var leaf = Path.GetFileName(dir);
            if (ExcludedFolderNames.Contains(leaf))
            {
                return true;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return false;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  HTML generation
    // ─────────────────────────────────────────────────────────────────────

    private async Task GenerateHtmlAsync(
        string folder,
        string htmlPath,
        Session session,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var docsMd = await ReadTextSafelyAsync(Path.Combine(folder, DocsMarkdownFileName), cancellationToken)
            .ConfigureAwait(false);
        var planMd = await ReadTextSafelyAsync(Path.Combine(folder, "plan.md"), cancellationToken)
            .ConfigureAwait(false);

        var mockups = EnumerateMockups(folder);
        var filesIndex = EnumerateFilesIndex(folder, mockups);
        var checkpoints = await GetCheckpointsAsync(session.Id, cancellationToken).ConfigureAwait(false);

        var html = RenderHtml(session, docsMd, planMd, mockups, filesIndex, checkpoints);

        var temp = htmlPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, html, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temp, htmlPath, overwrite: true);
            _logger.LogInformation("Regenerated {File} for session {Id}.", DocsHtmlFileName, session.Id);
        }
        catch
        {
            TryDeleteTemp(temp);
            throw;
        }
    }

    private async Task<string?> ReadTextSafelyAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxRenderableFileBytes)
            {
                _logger.LogDebug("Skipping {Path}: exceeds {Max} bytes.", path, MaxRenderableFileBytes);
                return null;
            }

            return await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read {Path}; rendering as missing.", path);
            return null;
        }
    }

    private List<MockupEntry> EnumerateMockups(string folder)
    {
        var results = new List<MockupEntry>();
        foreach (var sub in new[] { "files", "research" })
        {
            var subDir = Path.Combine(folder, sub);
            if (!Directory.Exists(subDir))
            {
                continue;
            }

            string[] htmlFiles;
            try
            {
                htmlFiles = Directory.GetFiles(subDir, "*.html", SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not enumerate mockups under {SubDir}.", subDir);
                continue;
            }

            foreach (var path in htmlFiles)
            {
                if (ShouldSkipFile(path, folder))
                {
                    continue;
                }

                long size = 0;
                try
                {
                    size = new FileInfo(path).Length;
                }
                catch
                {
                    // Best-effort; default to 0.
                }

                results.Add(new MockupEntry(
                    DisplayName: Path.GetFileName(path),
                    RelativePath: Path.GetRelativePath(folder, path).Replace('\\', '/'),
                    AbsolutePath: path,
                    Group: sub,
                    SizeBytes: size));
            }
        }

        return results
            .OrderBy(m => m.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<FileEntry> EnumerateFilesIndex(string folder, List<MockupEntry> mockups)
    {
        var mockupPaths = new HashSet<string>(
            mockups.Select(m => m.AbsolutePath),
            StringComparer.OrdinalIgnoreCase);

        var results = new List<FileEntry>();
        foreach (var sub in new[] { "files", "research" })
        {
            var subDir = Path.Combine(folder, sub);
            if (!Directory.Exists(subDir))
            {
                continue;
            }

            string[] entries;
            try
            {
                entries = Directory.GetFiles(subDir, "*", SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not enumerate files under {SubDir}.", subDir);
                continue;
            }

            foreach (var path in entries)
            {
                if (ShouldSkipFile(path, folder))
                {
                    continue;
                }

                if (mockupPaths.Contains(path))
                {
                    continue;
                }

                long size = 0;
                bool tooLarge = false;
                try
                {
                    size = new FileInfo(path).Length;
                    tooLarge = size > MaxRenderableFileBytes;
                }
                catch
                {
                    // Best-effort.
                }

                results.Add(new FileEntry(
                    DisplayName: Path.GetFileName(path),
                    RelativePath: Path.GetRelativePath(folder, path).Replace('\\', '/'),
                    AbsolutePath: path,
                    Group: sub,
                    SizeBytes: size,
                    TooLarge: tooLarge));
            }
        }

        return results
            .OrderBy(f => f.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<CheckpointEntry>> GetCheckpointsAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var summaries = await _folders.GetCheckpointsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var results = new List<CheckpointEntry>();
        foreach (var s in summaries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = await ReadTextSafelyAsync(s.FilePath, cancellationToken).ConfigureAwait(false);
            results.Add(new CheckpointEntry(s.Number, s.Title, s.FilePath, body));
        }

        // Newest first (reverse the natural ascending order from the reader).
        results.Reverse();
        return results;
    }

    private string RenderHtml(
        Session session,
        string? docsMd,
        string? planMd,
        IReadOnlyList<MockupEntry> mockups,
        IReadOnlyList<FileEntry> filesIndex,
        IReadOnlyList<CheckpointEntry> checkpoints)
    {
        var sb = new StringBuilder(64 * 1024);

        var displayName = !string.IsNullOrWhiteSpace(session.Summary) ? session.Summary! : session.Id;
        var generatedAt = _timeProvider.GetUtcNow().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.Append("<title>").Append(HtmlEncode(displayName)).AppendLine(" — session docs</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(InlineCss);
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        sb.AppendLine("<aside class=\"toc\">");
        sb.AppendLine("<h2>Contents</h2>");
        sb.AppendLine("<ul>");
        sb.AppendLine("<li><a href=\"#overview\">Overview</a></li>");
        sb.AppendLine("<li><a href=\"#mockups\">Mockups</a></li>");
        sb.AppendLine("<li><a href=\"#files\">Files</a></li>");
        sb.AppendLine("<li><a href=\"#plan\">Plan</a></li>");
        sb.AppendLine("<li><a href=\"#checkpoints\">Checkpoints</a></li>");
        sb.AppendLine("</ul>");
        sb.AppendLine("</aside>");

        sb.AppendLine("<main>");

        // Header
        sb.AppendLine("<header class=\"session-header\">");
        sb.Append("<h1>").Append(HtmlEncode(displayName)).AppendLine("</h1>");
        sb.AppendLine("<div class=\"meta\">");
        sb.Append("<span class=\"pill status\">").Append(HtmlEncode(session.Status.ToString())).AppendLine("</span>");
        if (!string.IsNullOrWhiteSpace(session.Repository))
        {
            sb.Append("<span class=\"chip\">").Append(HtmlEncode(session.Repository!));
            if (!string.IsNullOrWhiteSpace(session.Branch))
            {
                sb.Append(" · ").Append(HtmlEncode(session.Branch!));
            }
            sb.AppendLine("</span>");
        }
        if (!string.IsNullOrWhiteSpace(session.Producer))
        {
            sb.Append("<span class=\"chip\">").Append(HtmlEncode(session.Producer!)).AppendLine("</span>");
        }
        sb.Append("<span class=\"chip\">").Append(session.TurnCount.ToString(CultureInfo.InvariantCulture))
            .AppendLine(" turns</span>");
        sb.Append("<span class=\"chip subtle\">id ").Append(HtmlEncode(session.Id)).AppendLine("</span>");
        sb.AppendLine("</div>");
        sb.Append("<p class=\"generated\">Rendered by csm at ").Append(generatedAt).AppendLine(".</p>");
        sb.AppendLine("</header>");

        // Overview / user-curated
        sb.AppendLine("<section id=\"overview\" class=\"curated\">");
        if (!string.IsNullOrWhiteSpace(docsMd))
        {
            sb.AppendLine(Markdown.ToHtml(docsMd, _markdownPipeline));
        }
        else
        {
            sb.AppendLine("<p class=\"empty\">SESSION-DOCS.md not yet created — click 📚 Docs in the app to scaffold it.</p>");
        }
        sb.AppendLine("</section>");

        // Mockups
        sb.AppendLine("<section id=\"mockups\" class=\"auto\">");
        sb.AppendLine("<h2>Mockups <span class=\"auto-badge\">Auto-derived from session folder</span></h2>");
        if (mockups.Count == 0)
        {
            sb.AppendLine("<p class=\"empty\">No HTML artifacts found under <code>files/</code> or <code>research/</code>.</p>");
        }
        else
        {
            sb.AppendLine("<div class=\"mockup-grid\">");
            foreach (var m in mockups)
            {
                sb.AppendLine("<a class=\"mockup-card\" target=\"_blank\" rel=\"noopener\" href=\"")
                    .Append(ToFileUri(m.AbsolutePath)).Append("\">");
                sb.Append("<div class=\"mockup-name\">").Append(HtmlEncode(m.DisplayName)).AppendLine("</div>");
                sb.Append("<div class=\"mockup-meta\"><span class=\"pill folder\">").Append(HtmlEncode(m.Group))
                    .Append("</span> ").Append(FormatBytes(m.SizeBytes)).AppendLine("</div>");
                sb.AppendLine("<div class=\"mockup-open\">Open mockup ↗</div>");
                sb.AppendLine("</a>");
            }
            sb.AppendLine("</div>");
        }
        sb.AppendLine("</section>");

        // Files index
        sb.AppendLine("<section id=\"files\" class=\"auto\">");
        sb.AppendLine("<h2>Files <span class=\"auto-badge\">Auto-derived from session folder</span></h2>");
        if (filesIndex.Count == 0)
        {
            sb.AppendLine("<p class=\"empty\">No additional files under <code>files/</code> or <code>research/</code>.</p>");
        }
        else
        {
            string? currentGroup = null;
            sb.AppendLine("<ul class=\"files-list\">");
            foreach (var f in filesIndex)
            {
                if (!string.Equals(currentGroup, f.Group, StringComparison.OrdinalIgnoreCase))
                {
                    if (currentGroup is not null)
                    {
                        sb.AppendLine("</ul></li>");
                    }
                    currentGroup = f.Group;
                    sb.Append("<li class=\"files-group\"><span class=\"pill folder\">")
                        .Append(HtmlEncode(currentGroup)).AppendLine("</span><ul>");
                }

                sb.Append("<li><a target=\"_blank\" rel=\"noopener\" href=\"").Append(ToFileUri(f.AbsolutePath))
                    .Append("\">").Append(HtmlEncode(f.RelativePath)).Append("</a> ");
                sb.Append("<span class=\"size\">").Append(FormatBytes(f.SizeBytes)).Append("</span>");
                if (f.TooLarge)
                {
                    sb.Append(" <span class=\"warn\">(skipped — too large)</span>");
                }
                sb.AppendLine("</li>");
            }
            sb.AppendLine("</ul></li></ul>");
        }
        sb.AppendLine("</section>");

        // Plan
        sb.AppendLine("<section id=\"plan\" class=\"auto\">");
        sb.AppendLine("<h2>Plan <span class=\"auto-badge\">Auto-derived from plan.md</span></h2>");
        if (!string.IsNullOrWhiteSpace(planMd))
        {
            sb.AppendLine("<details open><summary>plan.md</summary>");
            sb.AppendLine("<div class=\"rendered-md\">");
            sb.AppendLine(Markdown.ToHtml(planMd, _markdownPipeline));
            sb.AppendLine("</div></details>");
        }
        else
        {
            sb.AppendLine("<p class=\"empty\">No <code>plan.md</code> in this session folder.</p>");
        }
        sb.AppendLine("</section>");

        // Checkpoints
        sb.AppendLine("<section id=\"checkpoints\" class=\"auto\">");
        sb.AppendLine("<h2>Checkpoints <span class=\"auto-badge\">Auto-derived from checkpoints/</span></h2>");
        if (checkpoints.Count == 0)
        {
            sb.AppendLine("<p class=\"empty\">No checkpoints recorded for this session.</p>");
        }
        else
        {
            foreach (var c in checkpoints)
            {
                sb.Append("<details><summary><span class=\"cp-num\">#")
                    .Append(c.Number.ToString("D3", CultureInfo.InvariantCulture))
                    .Append("</span> ").Append(HtmlEncode(c.Title)).AppendLine("</summary>");
                sb.AppendLine("<div class=\"rendered-md\">");
                if (!string.IsNullOrWhiteSpace(c.Body))
                {
                    sb.AppendLine(Markdown.ToHtml(c.Body!, _markdownPipeline));
                }
                else
                {
                    sb.AppendLine("<p class=\"empty\">(checkpoint body not readable)</p>");
                }
                sb.AppendLine("</div></details>");
            }
        }
        sb.AppendLine("</section>");

        sb.AppendLine("</main>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────

    private static string HtmlEncode(string? value) =>
        value is null ? string.Empty : WebUtility.HtmlEncode(value);

    /// <summary>
    /// Converts a Windows path to a <c>file://</c> URI suitable for an
    /// HTML <c>href</c>. Handles spaces and other reserved characters.
    /// </summary>
    internal static string ToFileUri(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        return new Uri(absolutePath).AbsoluteUri;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        if (bytes < 1024)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        if (bytes < 1024 * 1024)
        {
            return (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        }

        if (bytes < 1024L * 1024L * 1024L)
        {
            return (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        }

        return (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " GB";
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
            // Best-effort cleanup.
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Internal records
    // ─────────────────────────────────────────────────────────────────────

    private sealed record MockupEntry(
        string DisplayName,
        string RelativePath,
        string AbsolutePath,
        string Group,
        long SizeBytes);

    private sealed record FileEntry(
        string DisplayName,
        string RelativePath,
        string AbsolutePath,
        string Group,
        long SizeBytes,
        bool TooLarge);

    private sealed record CheckpointEntry(int Number, string Title, string FilePath, string? Body);

    // Catppuccin-mocha-aligned palette to match csm's WPF theme.
    private const string InlineCss = @"
* { box-sizing: border-box; }
body {
    margin: 0;
    background: #1e1e2e;
    color: #cdd6f4;
    font: 14px/1.55 -apple-system, ""Segoe UI"", Roboto, sans-serif;
    display: grid;
    grid-template-columns: 220px 1fr;
    min-height: 100vh;
}
a { color: #89b4fa; text-decoration: none; }
a:hover { text-decoration: underline; }
code, pre { font-family: Consolas, ""Cascadia Mono"", monospace; }
pre {
    background: #181825; padding: 12px; border-radius: 6px;
    overflow-x: auto; font-size: 12.5px; border: 1px solid #313244;
}
:not(pre) > code { background: #313244; padding: 1px 5px; border-radius: 3px; font-size: 12.5px; }
table { border-collapse: collapse; margin: 12px 0; }
th, td { border: 1px solid #313244; padding: 6px 10px; text-align: left; }
th { background: #181825; }
blockquote { border-left: 3px solid #89b4fa; margin: 12px 0; padding: 4px 12px; color: #a6adc8; background: #181825; }

aside.toc {
    background: #11111b; border-right: 1px solid #313244;
    padding: 24px 16px; position: sticky; top: 0; height: 100vh; overflow-y: auto;
}
aside.toc h2 { font-size: 11px; text-transform: uppercase; color: #7f849c; letter-spacing: 0.08em; margin: 0 0 12px; }
aside.toc ul { list-style: none; padding: 0; margin: 0; }
aside.toc li { margin: 4px 0; }
aside.toc a { color: #cdd6f4; font-size: 13px; }

main { padding: 32px 40px; max-width: 1100px; }
h1 { font-size: 24px; margin: 0 0 12px; }
h2 { font-size: 18px; margin: 32px 0 12px; padding-bottom: 8px; border-bottom: 1px solid #313244; display: flex; align-items: center; gap: 12px; }
h3 { font-size: 15px; margin: 20px 0 8px; }
p { margin: 8px 0; }

header.session-header { padding-bottom: 12px; border-bottom: 1px solid #313244; }
.meta { display: flex; flex-wrap: wrap; gap: 6px; margin: 8px 0; }
.pill, .chip {
    display: inline-block; padding: 2px 10px; border-radius: 3px; font-size: 11px; font-weight: 600;
}
.pill.status { background: #89b4fa; color: #11111b; }
.pill.folder { background: #f9e2af; color: #11111b; }
.chip { background: #313244; color: #cdd6f4; }
.chip.subtle { background: transparent; color: #7f849c; font-weight: 400; }
.generated { color: #7f849c; font-size: 11px; margin: 4px 0 0; }

.auto-badge {
    display: inline-block; background: #313244; color: #a6adc8;
    padding: 1px 8px; border-radius: 3px; font-size: 10px; font-weight: 500;
    text-transform: uppercase; letter-spacing: 0.05em;
}

.empty { color: #7f849c; font-style: italic; }
.warn { color: #f9e2af; font-size: 11px; }

.mockup-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 12px; }
.mockup-card {
    display: block; background: #181825; border: 1px solid #313244; border-radius: 6px;
    padding: 14px; color: #cdd6f4;
}
.mockup-card:hover { border-color: #89b4fa; text-decoration: none; }
.mockup-name { font-weight: 600; font-size: 13px; word-break: break-all; }
.mockup-meta { color: #7f849c; font-size: 11px; margin: 6px 0; }
.mockup-open { color: #89b4fa; font-size: 11px; }

.files-list { list-style: none; padding-left: 0; }
.files-list ul { list-style: none; padding-left: 16px; margin: 6px 0; }
.files-list li { margin: 3px 0; font-size: 13px; }
.files-list .size { color: #7f849c; font-size: 11px; margin-left: 6px; }
.files-group { margin: 12px 0; }

details {
    background: #181825; border: 1px solid #313244; border-radius: 6px;
    padding: 8px 14px; margin: 8px 0;
}
details[open] { padding-bottom: 14px; }
details > summary {
    cursor: pointer; font-weight: 600; padding: 4px 0;
    list-style-position: outside;
}
.cp-num { color: #f9e2af; font-family: Consolas, monospace; margin-right: 6px; }
.rendered-md { padding-top: 8px; }

@media (max-width: 800px) {
    body { grid-template-columns: 1fr; }
    aside.toc { position: static; height: auto; border-right: none; border-bottom: 1px solid #313244; }
    main { padding: 20px; }
}
";
}

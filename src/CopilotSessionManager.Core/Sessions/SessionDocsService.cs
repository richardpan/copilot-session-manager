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
    private readonly IReadOnlyList<ISessionDocsSectionProvider> _sectionProviders;
    private readonly TimeSpan _sectionProviderTimeout;

    /// <summary>V1.5 (#198): default per-provider timeout for <see cref="ISessionDocsSectionProvider.GetSectionsAsync"/>.</summary>
    public static readonly TimeSpan DefaultSectionProviderTimeout = TimeSpan.FromSeconds(2);

    public SessionDocsService(
        ISessionFolderReader folders,
        TimeProvider timeProvider,
        ILogger<SessionDocsService> logger,
        IEnumerable<ISessionDocsSectionProvider>? sectionProviders = null,
        TimeSpan? sectionProviderTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _folders = folders;
        _timeProvider = timeProvider;
        _logger = logger;
        _sectionProviders = sectionProviders is null
            ? Array.Empty<ISessionDocsSectionProvider>()
            : sectionProviders.ToArray();
        _sectionProviderTimeout = sectionProviderTimeout ?? DefaultSectionProviderTimeout;

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

    /// <summary>V1.5: <c>plan.md</c> lives alongside SESSION-DOCS.* in every session folder.</summary>
    public const string PlanMarkdownFileName = "plan.md";

    public string GetPlanMarkdownPath(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Path.Combine(_folders.GetSessionFolderPath(sessionId), PlanMarkdownFileName);
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

        // V1.5 (#196): drop-in fragments — SESSION-DOCS.<name>.md and
        // SESSION-DOCS.<name>.html at the session folder root. Each one
        // contributes its own <section> to the rendered HTML and counts
        // toward the staleness check so adding/editing one schedules a regen.
        foreach (var fragment in EnumerateFragmentFiles(folder))
        {
            yield return fragment.AbsolutePath;
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

    // ─────────────────────────────────────────────────────────────────────
    //  Drop-in fragment discovery (V1.5 #196)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// V1.5 (#196): file-name prefix used to detect drop-in documentation
    /// fragments. A file matching
    /// <c>SESSION-DOCS.&lt;name&gt;.&lt;md|html&gt;</c> at the session folder root
    /// becomes its own section in the rendered HTML. The main
    /// <c>SESSION-DOCS.md</c> source and the rendered
    /// <c>SESSION-DOCS.html</c> output are intentionally excluded.
    /// </summary>
    public const string FragmentFilePrefix = "SESSION-DOCS.";

    /// <summary>
    /// V1.5 (#196): the kinds of drop-in fragments csm understands.
    /// <list type="bullet">
    ///   <item><c>Markdown</c> — Markdig-rendered inline as a section body.</item>
    ///   <item><c>Html</c> — embedded via a sandboxed sibling <c>&lt;iframe&gt;</c>.</item>
    /// </list>
    /// </summary>
    public enum FragmentKind { Markdown, Html }

    /// <summary>V1.5 (#196): one drop-in fragment discovered in the session folder.</summary>
    public sealed record FragmentEntry(
        string Name,
        string DisplayTitle,
        string AbsolutePath,
        string FileName,
        FragmentKind Kind);

    /// <summary>V1.5 (#196): enumerate the drop-in fragments at the root of <paramref name="folder"/>.</summary>
    public List<FragmentEntry> EnumerateFragmentFiles(string folder)
    {
        var results = new List<FragmentEntry>();
        if (!Directory.Exists(folder))
        {
            return results;
        }

        string[] entries;
        try
        {
            entries = Directory.GetFiles(folder, FragmentFilePrefix + "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not enumerate fragments under {Folder}.", folder);
            return results;
        }

        foreach (var path in entries)
        {
            var fileName = Path.GetFileName(path);
            if (TryParseFragmentFileName(fileName, out var name, out var kind))
            {
                results.Add(new FragmentEntry(
                    Name: name,
                    DisplayTitle: PrettifyFragmentName(name),
                    AbsolutePath: path,
                    FileName: fileName,
                    Kind: kind));
            }
        }

        // Deterministic ordering: by display title, ASCII-insensitive,
        // so the user can guide ordering with numeric prefixes
        // (SESSION-DOCS.01-arch.md, SESSION-DOCS.02-tokens.md, …).
        return results
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// V1.5 (#196): parses <c>SESSION-DOCS.&lt;name&gt;.&lt;ext&gt;</c>. Returns
    /// <c>false</c> for the main <c>SESSION-DOCS.md</c> / generated
    /// <c>SESSION-DOCS.html</c> as well as anything with an unsupported
    /// extension or an empty <c>&lt;name&gt;</c> slot.
    /// </summary>
    public static bool TryParseFragmentFileName(
        string fileName,
        out string name,
        out FragmentKind kind)
    {
        name = string.Empty;
        kind = default;

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (!fileName.StartsWith(FragmentFilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Reject the bare SESSION-DOCS.md / SESSION-DOCS.html sources.
        if (string.Equals(fileName, DocsMarkdownFileName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, DocsHtmlFileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var ext = Path.GetExtension(fileName);
        if (string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase))
        {
            kind = FragmentKind.Markdown;
        }
        else if (string.Equals(ext, ".html", StringComparison.OrdinalIgnoreCase))
        {
            kind = FragmentKind.Html;
        }
        else
        {
            return false;
        }

        // Middle slice: everything between the prefix and the extension.
        var middle = fileName.Substring(
            FragmentFilePrefix.Length,
            fileName.Length - FragmentFilePrefix.Length - ext.Length);

        if (string.IsNullOrWhiteSpace(middle))
        {
            return false;
        }

        name = middle;
        return true;
    }

    /// <summary>
    /// V1.5 (#196): turn a slug like <c>token-burn</c> or <c>01-architecture</c>
    /// into a human-readable section title (<c>Token Burn</c>,
    /// <c>01 Architecture</c>). Leading numeric prefixes are preserved
    /// so users can use them to control ordering without polluting the title.
    /// </summary>
    public static string PrettifyFragmentName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var parts = name.Split(new[] { '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return name;
        }

        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            // Pure-digit chunks stay as-is so "01" doesn't become "01" via TextInfo.
            if (!p.All(char.IsDigit) && p.Length > 0 && char.IsLetter(p[0]))
            {
                parts[i] = char.ToUpper(p[0], CultureInfo.InvariantCulture) + p.Substring(1);
            }
        }

        return string.Join(' ', parts);
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

        var fragments = await LoadFragmentsAsync(folder, cancellationToken).ConfigureAwait(false);
        var mockups = EnumerateMockups(folder);
        var filesIndex = EnumerateFilesIndex(folder, mockups);
        var checkpoints = await GetCheckpointsAsync(session.Id, cancellationToken).ConfigureAwait(false);
        var providerSections = await LoadProviderSectionsAsync(session, cancellationToken).ConfigureAwait(false);

        var html = RenderHtml(session, docsMd, planMd, fragments, providerSections, mockups, filesIndex, checkpoints);

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

    private async Task<List<LoadedFragment>> LoadFragmentsAsync(
        string folder,
        CancellationToken cancellationToken)
    {
        var entries = EnumerateFragmentFiles(folder);
        var loaded = new List<LoadedFragment>(entries.Count);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Markdown fragments get inlined, so we have to read them.
            // HTML fragments are embedded via <iframe src=…> against the
            // sibling file on disk — we still bail if the file is too
            // large since a multi-MB iframe page is rarely intentional.
            string? body = null;
            if (entry.Kind == FragmentKind.Markdown)
            {
                body = await ReadTextSafelyAsync(entry.AbsolutePath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                try
                {
                    var size = new FileInfo(entry.AbsolutePath).Length;
                    if (size > MaxRenderableFileBytes)
                    {
                        _logger.LogWarning(
                            "Fragment {Path} exceeds {Max} bytes; skipping.",
                            entry.AbsolutePath, MaxRenderableFileBytes);
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not stat fragment {Path}; skipping.", entry.AbsolutePath);
                    continue;
                }
            }

            loaded.Add(new LoadedFragment(entry, body));
        }
        return loaded;
    }

    /// <summary>
    /// V1.5 (#198): runs every registered <see cref="ISessionDocsSectionProvider"/>
    /// in parallel with a per-provider timeout. Failures and timeouts are
    /// logged and skipped — they never block the render. Returned sections
    /// are bucketed by <see cref="SectionPlacement"/> for the renderer to
    /// emit at the right slot.
    /// </summary>
    private async Task<IReadOnlyDictionary<SectionPlacement, List<ResolvedSection>>> LoadProviderSectionsAsync(
        Session session,
        CancellationToken cancellationToken)
    {
        var buckets = new Dictionary<SectionPlacement, List<ResolvedSection>>();
        if (_sectionProviders.Count == 0)
        {
            return buckets;
        }

        var tasks = new List<Task<(ISessionDocsSectionProvider Provider, IReadOnlyList<DocsSection>? Sections)>>(_sectionProviders.Count);
        foreach (var provider in _sectionProviders)
        {
            tasks.Add(RunProviderAsync(provider, session, cancellationToken));
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var seenAnchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (provider, sections) in results)
        {
            if (sections is null)
            {
                continue;
            }

            for (var i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                if (section is null)
                {
                    continue;
                }

                var anchor = SlugifyAnchor(string.IsNullOrWhiteSpace(section.Anchor) ? provider.Name : section.Anchor);
                if (!seenAnchors.Add(anchor))
                {
                    // Disambiguate duplicate anchors so the page never has colliding ids.
                    var n = 2;
                    string candidate;
                    do
                    {
                        candidate = anchor + "-" + n.ToString(CultureInfo.InvariantCulture);
                        n++;
                    }
                    while (!seenAnchors.Add(candidate));
                    anchor = candidate;
                }

                if (!buckets.TryGetValue(section.Placement, out var list))
                {
                    list = new List<ResolvedSection>();
                    buckets[section.Placement] = list;
                }
                list.Add(new ResolvedSection(provider, section, anchor, i));
            }
        }

        // Stable per-bucket ordering: Provider.Order asc, Provider.Name asc, then the
        // original index within that provider's returned list.
        foreach (var list in buckets.Values)
        {
            list.Sort(static (a, b) =>
            {
                var cmp = a.Provider.Order.CompareTo(b.Provider.Order);
                if (cmp != 0)
                {
                    return cmp;
                }
                cmp = StringComparer.OrdinalIgnoreCase.Compare(a.Provider.Name, b.Provider.Name);
                return cmp != 0 ? cmp : a.ProviderIndex.CompareTo(b.ProviderIndex);
            });
        }

        return buckets;
    }

    private async Task<(ISessionDocsSectionProvider Provider, IReadOnlyList<DocsSection>? Sections)> RunProviderAsync(
        ISessionDocsSectionProvider provider,
        Session session,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_sectionProviderTimeout);
        try
        {
            var sections = await provider.GetSectionsAsync(session, cts.Token).ConfigureAwait(false);
            return (provider, sections);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Section provider {Provider} timed out after {Timeout}; skipping.",
                provider.Name, _sectionProviderTimeout);
            return (provider, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Section provider {Provider} threw; skipping.", provider.Name);
            return (provider, null);
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
        IReadOnlyList<LoadedFragment> fragments,
        IReadOnlyDictionary<SectionPlacement, List<ResolvedSection>> providerSections,
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
        AppendTocEntries(sb, providerSections, SectionPlacement.AfterOverview);
        foreach (var f in fragments)
        {
            sb.Append("<li><a href=\"#fragment-").Append(SlugifyAnchor(f.Entry.Name)).Append("\">")
                .Append(HtmlEncode(f.Entry.DisplayTitle)).AppendLine("</a></li>");
        }
        AppendTocEntries(sb, providerSections, SectionPlacement.AfterFragments);
        sb.AppendLine("<li><a href=\"#mockups\">Mockups</a></li>");
        AppendTocEntries(sb, providerSections, SectionPlacement.AfterMockups);
        sb.AppendLine("<li><a href=\"#files\">Files</a></li>");
        AppendTocEntries(sb, providerSections, SectionPlacement.AfterFiles);
        sb.AppendLine("<li><a href=\"#plan\">Plan</a></li>");
        AppendTocEntries(sb, providerSections, SectionPlacement.AfterPlan);
        sb.AppendLine("<li><a href=\"#checkpoints\">Checkpoints</a></li>");
        AppendTocEntries(sb, providerSections, SectionPlacement.End);
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

        AppendProviderSections(sb, providerSections, SectionPlacement.AfterOverview);

        // V1.5 (#196): drop-in fragments. Markdown is inlined; HTML is
        // embedded via a sibling <iframe src=…> so the host page stays
        // safe even if the fragment has its own <style> / <script>.
        foreach (var f in fragments)
        {
            var anchor = SlugifyAnchor(f.Entry.Name);
            sb.Append("<section id=\"fragment-").Append(anchor).AppendLine("\" class=\"curated fragment\">");
            sb.Append("<h2>").Append(HtmlEncode(f.Entry.DisplayTitle))
                .Append(" <span class=\"auto-badge\">").Append(HtmlEncode(f.Entry.FileName))
                .AppendLine("</span></h2>");

            if (f.Entry.Kind == FragmentKind.Markdown)
            {
                if (!string.IsNullOrWhiteSpace(f.Body))
                {
                    sb.AppendLine("<div class=\"rendered-md\">");
                    sb.AppendLine(Markdown.ToHtml(f.Body!, _markdownPipeline));
                    sb.AppendLine("</div>");
                }
                else
                {
                    sb.Append("<p class=\"empty\">(<code>").Append(HtmlEncode(f.Entry.FileName))
                        .AppendLine("</code> is empty or unreadable.)</p>");
                }
            }
            else
            {
                sb.Append("<iframe class=\"fragment-frame\" loading=\"lazy\" src=\"")
                    .Append(HtmlEncode(f.Entry.FileName))
                    .AppendLine("\" sandbox=\"allow-scripts allow-same-origin allow-popups\"></iframe>");
                sb.Append("<p class=\"fragment-link\"><a target=\"_blank\" rel=\"noopener\" href=\"")
                    .Append(HtmlEncode(f.Entry.FileName))
                    .AppendLine("\">Open fragment in new tab ↗</a></p>");
            }

            sb.AppendLine("</section>");
        }

        AppendProviderSections(sb, providerSections, SectionPlacement.AfterFragments);

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

        AppendProviderSections(sb, providerSections, SectionPlacement.AfterMockups);

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

        AppendProviderSections(sb, providerSections, SectionPlacement.AfterFiles);

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

        AppendProviderSections(sb, providerSections, SectionPlacement.AfterPlan);

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

        AppendProviderSections(sb, providerSections, SectionPlacement.End);

        sb.AppendLine("</main>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Provider-section helpers (V1.5 #198)
    // ─────────────────────────────────────────────────────────────────────

    private static void AppendTocEntries(
        StringBuilder sb,
        IReadOnlyDictionary<SectionPlacement, List<ResolvedSection>> sections,
        SectionPlacement placement)
    {
        if (!sections.TryGetValue(placement, out var list) || list.Count == 0)
        {
            return;
        }

        foreach (var resolved in list)
        {
            sb.Append("<li><a href=\"#").Append(resolved.Anchor).Append("\">")
                .Append(HtmlEncode(resolved.Section.Title)).AppendLine("</a></li>");
        }
    }

    private static void AppendProviderSections(
        StringBuilder sb,
        IReadOnlyDictionary<SectionPlacement, List<ResolvedSection>> sections,
        SectionPlacement placement)
    {
        if (!sections.TryGetValue(placement, out var list) || list.Count == 0)
        {
            return;
        }

        foreach (var resolved in list)
        {
            sb.Append("<section id=\"").Append(resolved.Anchor).AppendLine("\" class=\"auto provider\">");
            sb.Append("<h2>").Append(HtmlEncode(resolved.Section.Title));
            if (!string.IsNullOrWhiteSpace(resolved.Section.Subtitle))
            {
                sb.Append(" <span class=\"auto-badge\">").Append(HtmlEncode(resolved.Section.Subtitle!)).Append("</span>");
            }
            sb.AppendLine("</h2>");
            sb.AppendLine(resolved.Section.HtmlBody ?? string.Empty);
            sb.AppendLine("</section>");
        }
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

    /// <summary>V1.5 (#198): a <see cref="DocsSection"/> with its resolved anchor + provider context.</summary>
    private sealed record ResolvedSection(
        ISessionDocsSectionProvider Provider,
        DocsSection Section,
        string Anchor,
        int ProviderIndex);

    /// <summary>V1.5 (#196): fragment metadata + (for markdown) eagerly-loaded body.</summary>
    public sealed record LoadedFragment(FragmentEntry Entry, string? Body);

    /// <summary>
    /// V1.5 (#196): produce a stable, URL-safe anchor for a fragment name.
    /// Drops anything that is not [A-Za-z0-9_-] so it can sit inside an
    /// <c>id="fragment-…"</c> attribute and a <c>#fragment-…</c> link.
    /// </summary>
    public static string SlugifyAnchor(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (c == ' ' || c == '.')
            {
                sb.Append('-');
            }
        }
        var result = sb.ToString();
        return string.IsNullOrEmpty(result) ? "fragment" : result;
    }

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

/* V1.5 (#196): drop-in fragment sections. */
section.fragment {
    border: 1px solid #313244;
    border-radius: 8px;
    padding: 16px 20px;
    margin: 18px 0;
    background: #181825;
}
section.fragment > h2 { margin-top: 0; }
.fragment-frame {
    width: 100%;
    height: 540px;
    border: 1px solid #313244;
    border-radius: 6px;
    background: #11111b;
}
.fragment-link { margin: 8px 0 0; font-size: 12px; }
.fragment-link a { color: #89b4fa; }

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

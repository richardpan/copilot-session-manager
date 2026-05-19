using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Sessions;

/// <summary>
/// V1.6 (#118) tests for <see cref="SessionDocsService"/>: scaffold-once,
/// never-overwrite, never-touch SESSION-README, stale check, file:// URI
/// generation, internals filter, mockup gallery, graceful render when
/// SESSION-DOCS.md is missing.
/// </summary>
public sealed class SessionDocsServiceTests : IDisposable
{
    private readonly string _root;
    private readonly Mock<ISessionFolderReader> _folders = new();

    public SessionDocsServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csm-docs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            /* best-effort */
        }
    }

    private SessionDocsService CreateSut() => new(
        _folders.Object,
        TimeProvider.System,
        NullLogger<SessionDocsService>.Instance);

    private string CreateSessionFolder(string sessionId)
    {
        var path = Path.Combine(_root, sessionId);
        Directory.CreateDirectory(path);
        _folders.Setup(f => f.GetSessionFolderPath(sessionId)).Returns(path);
        _folders
            .Setup(f => f.GetCheckpointsAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SessionCheckpointSummary>());
        return path;
    }

    private static Session BuildSession(string id, string? summary = "My Session") =>
        new(
            Id: id,
            Cwd: @"C:\ws\repo",
            Repository: "owner/repo",
            Branch: "main",
            Summary: summary,
            HostType: "cli",
            CreatedAt: DateTimeOffset.UtcNow.AddHours(-1),
            UpdatedAt: DateTimeOffset.UtcNow,
            TurnCount: 3,
            Status: SessionStatus.Idle,
            CopilotVersion: CopilotVersion.Zero,
            Locks: Array.Empty<SessionLockInfo>());

    [Fact]
    public void GetDocsMarkdownPath_ReturnsExpectedFileName()
    {
        var folder = CreateSessionFolder("abc");
        var sut = CreateSut();

        sut.GetDocsMarkdownPath("abc")
            .Should().Be(Path.Combine(folder, SessionDocsService.DocsMarkdownFileName));
    }

    [Fact]
    public void GetDocsHtmlPath_ReturnsExpectedFileName()
    {
        var folder = CreateSessionFolder("abc");
        var sut = CreateSut();

        sut.GetDocsHtmlPath("abc")
            .Should().Be(Path.Combine(folder, SessionDocsService.DocsHtmlFileName));
    }

    [Fact]
    public async Task EnsureAsync_ScaffoldsMarkdown_WhenMissing()
    {
        var folder = CreateSessionFolder("abc");
        var sut = CreateSut();

        var htmlPath = await sut.EnsureAsync(BuildSession("abc"));

        var mdPath = Path.Combine(folder, SessionDocsService.DocsMarkdownFileName);
        File.Exists(mdPath).Should().BeTrue();
        File.Exists(htmlPath).Should().BeTrue();

        var md = await File.ReadAllTextAsync(mdPath);
        md.Should().Contain("This file is managed by Copilot Session Manager (csm).");
        md.Should().Contain("csm will NEVER overwrite your changes.");
        md.Should().Contain("# My Session");
        md.Should().Contain("## Overview");
        md.Should().Contain("## Decisions");
        md.Should().Contain("## Features");
        md.Should().Contain("## Expected behavior");
        md.Should().Contain("## Mockups");
        md.Should().Contain("## Notes");
    }

    [Fact]
    public async Task EnsureAsync_DoesNotOverwriteExistingMarkdown()
    {
        var folder = CreateSessionFolder("abc");
        var mdPath = Path.Combine(folder, SessionDocsService.DocsMarkdownFileName);
        const string userContent = "# My custom title\n\nUser-edited content. Do not touch.";
        await File.WriteAllTextAsync(mdPath, userContent);
        var beforeMtime = File.GetLastWriteTimeUtc(mdPath);

        // Wait a tick so any rewrite would have a different mtime.
        await Task.Delay(50);

        var sut = CreateSut();
        await sut.EnsureAsync(BuildSession("abc"));

        var actual = await File.ReadAllTextAsync(mdPath);
        actual.Should().Be(userContent, "csm must never overwrite SESSION-DOCS.md once it exists");
        File.GetLastWriteTimeUtc(mdPath).Should().Be(beforeMtime,
            "the mtime must remain identical when csm refuses to overwrite");
    }

    [Fact]
    public async Task EnsureAsync_DoesNotTouchSessionReadmeMarkdown()
    {
        var folder = CreateSessionFolder("abc");
        var readmePath = Path.Combine(folder, "SESSION-README.md");
        await File.WriteAllTextAsync(readmePath, "# Existing README\n\nDo not touch.");
        var beforeMtime = File.GetLastWriteTimeUtc(readmePath);
        var beforeContent = await File.ReadAllTextAsync(readmePath);

        await Task.Delay(50);

        var sut = CreateSut();
        await sut.EnsureAsync(BuildSession("abc"));

        File.Exists(readmePath).Should().BeTrue();
        (await File.ReadAllTextAsync(readmePath)).Should().Be(beforeContent);
        File.GetLastWriteTimeUtc(readmePath).Should().Be(beforeMtime,
            "SESSION-README.md is owned by the existing README pipeline; SessionDocsService must never touch it");
    }

    [Fact]
    public async Task EnsureAsync_RegeneratesHtml_WhenSourceFileIsNewer()
    {
        var folder = CreateSessionFolder("abc");
        var sut = CreateSut();
        var htmlPath = await sut.EnsureAsync(BuildSession("abc"));
        var firstRenderMtime = File.GetLastWriteTimeUtc(htmlPath);

        await Task.Delay(1100);

        // Touch a source file: bump mtime on SESSION-DOCS.md (allowed,
        // simulating a user edit).
        var mdPath = Path.Combine(folder, SessionDocsService.DocsMarkdownFileName);
        File.SetLastWriteTimeUtc(mdPath, DateTime.UtcNow);

        await sut.EnsureAsync(BuildSession("abc"));

        File.GetLastWriteTimeUtc(htmlPath).Should().BeAfter(firstRenderMtime,
            "the HTML must be regenerated when its source markdown is newer");
    }

    [Fact]
    public async Task EnsureAsync_NoOp_WhenHtmlIsCurrent()
    {
        var folder = CreateSessionFolder("abc");
        var sut = CreateSut();
        var htmlPath = await sut.EnsureAsync(BuildSession("abc"));
        var firstRenderMtime = File.GetLastWriteTimeUtc(htmlPath);

        await Task.Delay(1100);

        await sut.EnsureAsync(BuildSession("abc"));

        File.GetLastWriteTimeUtc(htmlPath).Should().Be(firstRenderMtime,
            "the HTML must NOT be regenerated when no source has changed");
    }

    [Fact]
    public async Task EnsureAsync_GeneratesValidHtml5_WithSectionsAndCss()
    {
        var folder = CreateSessionFolder("abc");
        // Add a mockup so the gallery section renders something.
        Directory.CreateDirectory(Path.Combine(folder, "files"));
        await File.WriteAllTextAsync(Path.Combine(folder, "files", "mock.html"), "<html />");

        var sut = CreateSut();
        var htmlPath = await sut.EnsureAsync(BuildSession("abc"));
        var html = await File.ReadAllTextAsync(htmlPath);

        html.Should().StartWith("<!DOCTYPE html>", "the rendered file must be valid HTML5");
        html.Should().Contain("<html", because: "HTML root element is required");
        html.Should().Contain("<style", because: "CSS must be inlined for self-containment");
        html.Should().Contain("My Session", because: "the session display name should be in the header");
    }

    [Fact]
    public async Task EnsureAsync_MockupGallery_OnlyIncludesHtmlUnderFilesAndResearch()
    {
        var folder = CreateSessionFolder("abc");
        Directory.CreateDirectory(Path.Combine(folder, "files"));
        Directory.CreateDirectory(Path.Combine(folder, "research"));
        Directory.CreateDirectory(Path.Combine(folder, "checkpoints"));

        await File.WriteAllTextAsync(Path.Combine(folder, "files", "ui-mockup.html"), "<html />");
        await File.WriteAllTextAsync(Path.Combine(folder, "research", "spike.html"), "<html />");
        await File.WriteAllTextAsync(Path.Combine(folder, "files", "notes.md"), "# notes");
        await File.WriteAllTextAsync(Path.Combine(folder, "checkpoints", "001.html"), "<html />");

        var sut = CreateSut();
        var htmlPath = await sut.EnsureAsync(BuildSession("abc"));
        var html = await File.ReadAllTextAsync(htmlPath);

        html.Should().Contain("ui-mockup.html");
        html.Should().Contain("spike.html");
        html.Should().NotContain(">001.html<",
            "checkpoints/*.html must NOT appear in the mockup gallery");
    }

    [Fact]
    public async Task EnsureAsync_FilesIndex_SkipsInternalsAndExcludedFolders()
    {
        var folder = CreateSessionFolder("abc");
        Directory.CreateDirectory(Path.Combine(folder, "files"));
        Directory.CreateDirectory(Path.Combine(folder, "rewind-snapshots"));
        Directory.CreateDirectory(Path.Combine(folder, ".git"));

        // Internals at the session root — must never appear.
        await File.WriteAllTextAsync(Path.Combine(folder, "events.jsonl"), "{}");
        await File.WriteAllTextAsync(Path.Combine(folder, "session.db"), "x");
        await File.WriteAllTextAsync(Path.Combine(folder, "vscode.metadata.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(folder, "workspace.yaml"), "x");
        await File.WriteAllTextAsync(Path.Combine(folder, "inuse.foo.lock"), "x");
        // Inside excluded folders.
        await File.WriteAllTextAsync(Path.Combine(folder, "rewind-snapshots", "snap1.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(folder, ".git", "HEAD"), "ref");

        // A legit file that SHOULD appear.
        await File.WriteAllTextAsync(Path.Combine(folder, "files", "notes.md"), "# notes");

        var sut = CreateSut();
        var htmlPath = await sut.EnsureAsync(BuildSession("abc"));
        var html = await File.ReadAllTextAsync(htmlPath);

        html.Should().Contain("notes.md", "user files in files/ must be listed");

        html.Should().NotContain("events.jsonl");
        html.Should().NotContain("session.db");
        html.Should().NotContain("vscode.metadata.json");
        html.Should().NotContain("workspace.yaml");
        html.Should().NotContain("inuse.foo.lock");
        html.Should().NotContain("rewind-snapshots");
        html.Should().NotContain(".git");
    }

    [Fact]
    public async Task EnsureAsync_RendersGracefully_WhenScaffoldDeletedAfter()
    {
        var folder = CreateSessionFolder("abc");
        var sut = CreateSut();

        // First Ensure — scaffolds the markdown.
        await sut.EnsureAsync(BuildSession("abc"));

        // Simulate the user deleting SESSION-DOCS.md outside the app.
        File.Delete(Path.Combine(folder, SessionDocsService.DocsMarkdownFileName));
        File.Delete(Path.Combine(folder, SessionDocsService.DocsHtmlFileName));

        // Second Ensure — must re-scaffold the markdown and re-render the HTML.
        var htmlPath = await sut.EnsureAsync(BuildSession("abc"));

        File.Exists(Path.Combine(folder, SessionDocsService.DocsMarkdownFileName)).Should().BeTrue();
        File.Exists(htmlPath).Should().BeTrue();
    }

    [Fact]
    public async Task EnsureAsync_GeneratesPercentEncodedFileUris_ForFilesWithSpaces()
    {
        var folder = CreateSessionFolder("abc");
        Directory.CreateDirectory(Path.Combine(folder, "files"));
        // Filename with a literal space — must be percent-encoded in the
        // file:// URI emitted by the HTML generator.
        await File.WriteAllTextAsync(Path.Combine(folder, "files", "ui mockup.html"), "<html />");

        var sut = CreateSut();
        var htmlPath = await sut.EnsureAsync(BuildSession("abc"));
        var html = await File.ReadAllTextAsync(htmlPath);

        html.Should().Contain("ui%20mockup.html",
            "files with spaces must be referenced via percent-encoded file:// URIs");
        html.Should().NotContain("file:///" + folder.Replace('\\', '/') + "/files/ui mockup.html",
            "raw spaces inside an href value would yield a malformed URL");
    }

    // ─────────────────────────────────────────────────────────────────────
    //  V1.5 (#196): drop-in fragment plumbing
    // ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("SESSION-DOCS.architecture.md", "architecture")]
    [InlineData("SESSION-DOCS.token-burn.md", "token-burn")]
    [InlineData("SESSION-DOCS.01-arch.md", "01-arch")]
    [InlineData("session-docs.UPPER.HTML", "UPPER")]
    public void TryParseFragmentFileName_AcceptsValidFragments(string fileName, string expectedName)
    {
        var ok = SessionDocsService.TryParseFragmentFileName(fileName, out var name, out _);
        ok.Should().BeTrue();
        name.Should().Be(expectedName);
    }

    [Theory]
    [InlineData("SESSION-DOCS.md")]
    [InlineData("SESSION-DOCS.html")]
    [InlineData("SESSION-DOCS..md")]
    [InlineData("SESSION-DOCS.txt")]
    [InlineData("SESSION-DOCS.foo.txt")]
    [InlineData("SESSION-README.md")]
    [InlineData("")]
    [InlineData("plan.md")]
    public void TryParseFragmentFileName_RejectsNonFragmentFileNames(string fileName)
    {
        var ok = SessionDocsService.TryParseFragmentFileName(fileName, out _, out _);
        ok.Should().BeFalse();
    }

    [Fact]
    public void TryParseFragmentFileName_RoutesByExtension()
    {
        SessionDocsService.TryParseFragmentFileName("SESSION-DOCS.x.md", out _, out var mdKind)
            .Should().BeTrue();
        mdKind.ToString().Should().Be("Markdown");

        SessionDocsService.TryParseFragmentFileName("SESSION-DOCS.x.html", out _, out var htmlKind)
            .Should().BeTrue();
        htmlKind.ToString().Should().Be("Html");
    }

    [Theory]
    [InlineData("architecture", "Architecture")]
    [InlineData("token-burn", "Token Burn")]
    [InlineData("01-architecture", "01 Architecture")]
    [InlineData("system_overview", "System Overview")]
    [InlineData("foo.bar.baz", "Foo Bar Baz")]
    public void PrettifyFragmentName_PreservesNumericPrefixesAndTitleCasesWords(string input, string expected)
    {
        SessionDocsService.PrettifyFragmentName(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("architecture", "architecture")]
    [InlineData("Token-Burn", "token-burn")]
    [InlineData("01-arch v2", "01-arch-v2")]
    [InlineData("with/slash", "withslash")]
    public void SlugifyAnchor_ProducesUrlSafeLowercaseSlug(string input, string expected)
    {
        SessionDocsService.SlugifyAnchor(input).Should().Be(expected);
    }

    [Fact]
    public async Task EnsureAsync_RendersMarkdownFragmentAsInlineSection()
    {
        var folder = CreateSessionFolder("abc");
        await File.WriteAllTextAsync(
            Path.Combine(folder, "SESSION-DOCS.architecture.md"),
            "## Layers\n\n- presentation\n- domain\n- data\n");

        var sut = CreateSut();
        var htmlPath = await sut.EnsureAsync(BuildSession("abc"));
        var html = await File.ReadAllTextAsync(htmlPath);

        html.Should().Contain("id=\"fragment-architecture\"",
            "the markdown fragment gets its own anchored section");
        html.Should().Contain(">Architecture<",
            "the fragment heading is title-cased from the slug");
        html.Should().Contain("SESSION-DOCS.architecture.md",
            "the fragment section labels the source file in its badge");
        html.Should().Contain("<li><a href=\"#fragment-architecture\">Architecture</a></li>",
            "the TOC must link to the fragment anchor");
        html.Should().Contain("<li>presentation</li>",
            "Markdig must inline-render the fragment markdown body");
    }

    [Fact]
    public async Task EnsureAsync_EmbedsHtmlFragmentViaSandboxedIframe_PointingAtSiblingFile()
    {
        var folder = CreateSessionFolder("abc");
        await File.WriteAllTextAsync(
            Path.Combine(folder, "SESSION-DOCS.diagram.html"),
            "<html><body><svg></svg></body></html>");

        var sut = CreateSut();
        var htmlPath = await sut.EnsureAsync(BuildSession("abc"));
        var html = await File.ReadAllTextAsync(htmlPath);

        html.Should().Contain("<iframe class=\"fragment-frame\"",
            "html fragments must be embedded via an iframe so their <style>/<script> don't bleed");
        html.Should().Contain("src=\"SESSION-DOCS.diagram.html\"",
            "the iframe must point at the sibling file by name, not at the embedded body");
        html.Should().Contain("sandbox=\"allow-scripts allow-same-origin allow-popups\"",
            "the iframe must declare an explicit sandbox policy");
        html.Should().NotContain("<svg></svg>",
            "html fragment body content must NOT be inlined into the parent document");
    }

    [Fact]
    public async Task EnsureAsync_OrdersFragmentsByName()
    {
        var folder = CreateSessionFolder("abc");
        await File.WriteAllTextAsync(Path.Combine(folder, "SESSION-DOCS.02-tokens.md"), "tokens");
        await File.WriteAllTextAsync(Path.Combine(folder, "SESSION-DOCS.01-architecture.md"), "arch");
        await File.WriteAllTextAsync(Path.Combine(folder, "SESSION-DOCS.03-research.html"), "<html />");

        var sut = CreateSut();
        var htmlPath = await sut.EnsureAsync(BuildSession("abc"));
        var html = await File.ReadAllTextAsync(htmlPath);

        var archIndex = html.IndexOf("fragment-01-architecture", StringComparison.Ordinal);
        var tokensIndex = html.IndexOf("fragment-02-tokens", StringComparison.Ordinal);
        var researchIndex = html.IndexOf("fragment-03-research", StringComparison.Ordinal);

        archIndex.Should().BeGreaterThan(0);
        tokensIndex.Should().BeGreaterThan(archIndex,
            "fragments must render in alphanumeric order — 02 after 01");
        researchIndex.Should().BeGreaterThan(tokensIndex,
            "fragments must render in alphanumeric order — 03 after 02");
    }

    [Fact]
    public async Task EnsureAsync_IgnoresMainSessionDocsMdAndGeneratedHtml_AsFragments()
    {
        var folder = CreateSessionFolder("abc");
        var sut = CreateSut();

        // Run once to scaffold SESSION-DOCS.md and generate SESSION-DOCS.html.
        var htmlPath = await sut.EnsureAsync(BuildSession("abc"));
        File.Exists(htmlPath).Should().BeTrue();

        // Bump a source so the second EnsureAsync regenerates.
        await Task.Delay(1100); // mtimes are second-resolution on some FS — keep it generous
        await File.WriteAllTextAsync(Path.Combine(folder, "plan.md"), "p");

        var html = await File.ReadAllTextAsync(htmlPath);
        html.Should().NotContain("id=\"fragment-md\"", "the main SESSION-DOCS.md must NOT be treated as a fragment");
        html.Should().NotContain("id=\"fragment-html\"", "the generated SESSION-DOCS.html must NOT be treated as a fragment");
    }

    [Fact]
    public async Task EnsureAsync_RegeneratesHtml_WhenFragmentMtimeIsNewer()
    {
        var folder = CreateSessionFolder("abc");
        var sut = CreateSut();

        // First render — no fragments yet.
        var htmlPath = await sut.EnsureAsync(BuildSession("abc"));
        var first = await File.ReadAllTextAsync(htmlPath);
        first.Should().NotContain("fragment-architecture",
            "no fragment has been added at this point");

        // Wait for clock to tick past the html mtime, then add a fragment.
        await Task.Delay(1100);
        await File.WriteAllTextAsync(
            Path.Combine(folder, "SESSION-DOCS.architecture.md"),
            "# Layers");

        // Second EnsureAsync MUST observe the new fragment as a "newer source"
        // and regenerate — otherwise dropping in a fragment would have no
        // effect until something else got touched.
        var htmlPathAgain = await sut.EnsureAsync(BuildSession("abc"));
        htmlPathAgain.Should().Be(htmlPath);
        var second = await File.ReadAllTextAsync(htmlPath);

        second.Should().Contain("fragment-architecture",
            "adding a fragment file must make NeedsRegeneration return true");
    }

    // ─────────────────────────────────────────────────────────────────────
    //  V1.5 (#198): ISessionDocsSectionProvider integration tests
    // ─────────────────────────────────────────────────────────────────────

    private SessionDocsService CreateSutWithProviders(
        IEnumerable<ISessionDocsSectionProvider> providers,
        TimeSpan? providerTimeout = null) => new(
        _folders.Object,
        TimeProvider.System,
        NullLogger<SessionDocsService>.Instance,
        providers,
        providerTimeout);

    [Fact]
    public async Task EnsureAsync_NoSectionProviders_RendersExistingSectionsUnchanged()
    {
        var folder = CreateSessionFolder("abc");
        var baseline = CreateSut();
        var baselinePath = await baseline.EnsureAsync(BuildSession("abc"));
        var baselineHtml = await File.ReadAllTextAsync(baselinePath);

        // Recreate folder + tweak a different session id so the second
        // render is independent of the first generator's stale check.
        var folder2 = CreateSessionFolder("def");
        var withEmpty = CreateSutWithProviders(Array.Empty<ISessionDocsSectionProvider>());
        var emptyPath = await withEmpty.EnsureAsync(BuildSession("def", summary: "My Session"));
        var emptyHtml = await File.ReadAllTextAsync(emptyPath);

        // Strip the session-id chips so we can compare structurally.
        baselineHtml.Replace("id abc", "id X").Should().Be(emptyHtml.Replace("id def", "id X"),
            "an empty provider list must not change the rendered output");
    }

    [Fact]
    public async Task EnsureAsync_SectionProvider_AppendsSectionAndTocEntry_AtRequestedPlacement()
    {
        var folder = CreateSessionFolder("abc");
        var provider = new FakeSectionProvider("git-status", order: 10, sections: _ => new[]
        {
            new DocsSection(
                Anchor: "git-status",
                Title: "Git status",
                HtmlBody: "<p class=\"git\">branch: main · clean</p>",
                Placement: SectionPlacement.AfterPlan,
                Subtitle: "From git"),
        });

        var sut = CreateSutWithProviders(new[] { provider });
        var htmlPath = await sut.EnsureAsync(BuildSession("abc"));
        var html = await File.ReadAllTextAsync(htmlPath);

        html.Should().Contain("<li><a href=\"#git-status\">Git status</a></li>",
            "TOC must include an entry for the provider section");
        html.Should().Contain("id=\"git-status\"", "section must use the requested anchor");
        html.Should().Contain("<p class=\"git\">branch: main · clean</p>",
            "HtmlBody is emitted verbatim — providers own escaping");
        html.Should().Contain("From git", "Subtitle renders as the auto-badge");

        // Slot check: section sits between Plan and Checkpoints in the body.
        var planIdx = html.IndexOf("id=\"plan\"", StringComparison.Ordinal);
        var providerIdx = html.IndexOf("id=\"git-status\"", StringComparison.Ordinal);
        var checkpointsIdx = html.IndexOf("id=\"checkpoints\"", StringComparison.Ordinal);
        planIdx.Should().BeLessThan(providerIdx, "AfterPlan must follow the Plan section");
        providerIdx.Should().BeLessThan(checkpointsIdx, "AfterPlan must precede Checkpoints");
    }

    [Theory]
    [InlineData(SectionPlacement.AfterOverview, "overview", "fragment-")] // appears between Overview and Fragments
    [InlineData(SectionPlacement.AfterMockups, "mockups", "files")]
    [InlineData(SectionPlacement.AfterFiles, "files", "plan")]
    [InlineData(SectionPlacement.End, "checkpoints", null)] // after the last built-in
    public async Task EnsureAsync_SectionProviderPlacement_SlotsAtCorrectBoundary(
        SectionPlacement placement, string previousAnchor, string? nextAnchorPrefix)
    {
        var folder = CreateSessionFolder("abc");

        // Add a fragment file so AfterOverview / AfterFragments tests have
        // a distinguishable boundary.
        await File.WriteAllTextAsync(
            Path.Combine(folder, "SESSION-DOCS.notes.md"), "# notes\n");

        var provider = new FakeSectionProvider("custom", order: 0, sections: _ => new[]
        {
            new DocsSection(
                Anchor: "custom-section",
                Title: "Custom",
                HtmlBody: "<p>body</p>",
                Placement: placement),
        });

        var sut = CreateSutWithProviders(new[] { provider });
        var htmlPath = await sut.EnsureAsync(BuildSession("abc"));
        var html = await File.ReadAllTextAsync(htmlPath);

        var prevIdx = html.IndexOf("id=\"" + previousAnchor + "\"", StringComparison.Ordinal);
        var customIdx = html.IndexOf("id=\"custom-section\"", StringComparison.Ordinal);
        prevIdx.Should().BeGreaterThan(-1);
        customIdx.Should().BeGreaterThan(prevIdx,
            $"custom section must appear after #{previousAnchor} for placement {placement}");

        if (nextAnchorPrefix is not null)
        {
            var nextIdx = html.IndexOf("id=\"" + nextAnchorPrefix, StringComparison.Ordinal);
            nextIdx.Should().BeGreaterThan(-1);
            customIdx.Should().BeLessThan(nextIdx,
                $"custom section must appear before id starting with '{nextAnchorPrefix}' for placement {placement}");
        }
    }

    [Fact]
    public async Task EnsureAsync_MultipleProviders_OrderedByProviderOrderThenName()
    {
        var folder = CreateSessionFolder("abc");
        var alpha = new FakeSectionProvider("alpha", order: 5, sections: _ => new[]
        {
            new DocsSection("alpha-1", "Alpha", "<p>a</p>", SectionPlacement.End),
        });
        var bravo = new FakeSectionProvider("bravo", order: 1, sections: _ => new[]
        {
            new DocsSection("bravo-1", "Bravo", "<p>b</p>", SectionPlacement.End),
        });
        // Same order as alpha — name should tie-break (alpha < charlie).
        var charlie = new FakeSectionProvider("charlie", order: 5, sections: _ => new[]
        {
            new DocsSection("charlie-1", "Charlie", "<p>c</p>", SectionPlacement.End),
        });

        var sut = CreateSutWithProviders(new[] { alpha, bravo, charlie });
        var htmlPath = await sut.EnsureAsync(BuildSession("abc"));
        var html = await File.ReadAllTextAsync(htmlPath);

        var bravoIdx = html.IndexOf("id=\"bravo-1\"", StringComparison.Ordinal);
        var alphaIdx = html.IndexOf("id=\"alpha-1\"", StringComparison.Ordinal);
        var charlieIdx = html.IndexOf("id=\"charlie-1\"", StringComparison.Ordinal);

        bravoIdx.Should().BeLessThan(alphaIdx, "lower Order wins");
        alphaIdx.Should().BeLessThan(charlieIdx, "name tie-breaks within the same Order");
    }

    [Fact]
    public async Task EnsureAsync_ThrowingProvider_IsSkippedAndOtherProvidersStillRender()
    {
        var folder = CreateSessionFolder("abc");
        var thrower = new FakeSectionProvider("thrower", order: 0, sections: _ =>
            throw new InvalidOperationException("kaboom"));
        var good = new FakeSectionProvider("good", order: 1, sections: _ => new[]
        {
            new DocsSection("good-section", "Good", "<p>still here</p>", SectionPlacement.End),
        });

        var sut = CreateSutWithProviders(new[] { thrower, good });
        var htmlPath = await sut.EnsureAsync(BuildSession("abc"));
        var html = await File.ReadAllTextAsync(htmlPath);

        html.Should().Contain("id=\"good-section\"",
            "a failing provider must not block other providers");
        html.Should().Contain("still here");
        html.Should().NotContain("kaboom", "provider exceptions must not leak into the rendered HTML");
    }

    [Fact]
    public async Task EnsureAsync_SlowProvider_IsTimedOutAndSkipped()
    {
        var folder = CreateSessionFolder("abc");
        var slow = new FakeSectionProvider(
            "slow",
            order: 0,
            sectionsAsync: async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return new[]
                {
                    new DocsSection("never-rendered", "Should not appear", "<p>nope</p>", SectionPlacement.End),
                };
            });
        var fast = new FakeSectionProvider("fast", order: 1, sections: _ => new[]
        {
            new DocsSection("fast-section", "Fast", "<p>here</p>", SectionPlacement.End),
        });

        var sut = CreateSutWithProviders(new[] { slow, fast }, providerTimeout: TimeSpan.FromMilliseconds(100));
        var htmlPath = await sut.EnsureAsync(BuildSession("abc"));
        var html = await File.ReadAllTextAsync(htmlPath);

        html.Should().NotContain("never-rendered", "slow providers must time out cleanly");
        html.Should().Contain("id=\"fast-section\"",
            "a slow provider must not block other providers");
    }

    [Fact]
    public async Task EnsureAsync_DuplicateAnchors_AreDisambiguated()
    {
        var folder = CreateSessionFolder("abc");
        var a = new FakeSectionProvider("a", order: 0, sections: _ => new[]
        {
            new DocsSection("info", "Info A", "<p>a</p>", SectionPlacement.End),
        });
        var b = new FakeSectionProvider("b", order: 1, sections: _ => new[]
        {
            new DocsSection("info", "Info B", "<p>b</p>", SectionPlacement.End),
        });

        var sut = CreateSutWithProviders(new[] { a, b });
        var htmlPath = await sut.EnsureAsync(BuildSession("abc"));
        var html = await File.ReadAllTextAsync(htmlPath);

        html.Should().Contain("id=\"info\"", "first collision wins the bare anchor");
        html.Should().Contain("id=\"info-2\"", "second collision is suffixed");
        html.Should().Contain("Info A");
        html.Should().Contain("Info B");
    }

    [Fact]
    public async Task EnsureAsync_SessionMetadataProvider_RendersDl_AtEnd()
    {
        var folder = CreateSessionFolder("abc");
        var sut = CreateSutWithProviders(new[] { new SessionMetadataSectionProvider() });
        var htmlPath = await sut.EnsureAsync(BuildSession("abc"));
        var html = await File.ReadAllTextAsync(htmlPath);

        html.Should().Contain("id=\"session-info\"", "reference provider must contribute its section");
        html.Should().Contain("<dl class=\"session-info\">");
        html.Should().Contain("<dt>Id</dt><dd>abc</dd>");
        html.Should().Contain("<dt>Repository</dt><dd>owner/repo</dd>");
        html.Should().Contain("<dt>Branch</dt><dd>main</dd>");
        html.Should().Contain("Auto-derived from session metadata", "subtitle renders as the badge");

        // Slot check: section appears after the Checkpoints section.
        var checkpointsIdx = html.IndexOf("id=\"checkpoints\"", StringComparison.Ordinal);
        var infoIdx = html.IndexOf("id=\"session-info\"", StringComparison.Ordinal);
        infoIdx.Should().BeGreaterThan(checkpointsIdx, "End placement must follow Checkpoints");
    }

    [Fact]
    public void SessionMetadataProvider_GetSectionsAsync_NullSession_Throws()
    {
        var provider = new SessionMetadataSectionProvider();
        FluentActions.Invoking(() => provider.GetSectionsAsync(null!, CancellationToken.None).AsTask())
            .Should().ThrowAsync<ArgumentNullException>();
    }

    /// <summary>Test double covering both sync and async provider shapes.</summary>
    private sealed class FakeSectionProvider : ISessionDocsSectionProvider
    {
        private readonly Func<Session, IReadOnlyList<DocsSection>>? _sync;
        private readonly Func<CancellationToken, Task<IReadOnlyList<DocsSection>>>? _async;

        public FakeSectionProvider(string name, int order, Func<Session, IReadOnlyList<DocsSection>> sections)
        {
            Name = name;
            Order = order;
            _sync = sections;
        }

        public FakeSectionProvider(string name, int order, Func<CancellationToken, Task<IReadOnlyList<DocsSection>>> sectionsAsync)
        {
            Name = name;
            Order = order;
            _async = sectionsAsync;
        }

        public string Name { get; }
        public int Order { get; }

        public async ValueTask<IReadOnlyList<DocsSection>> GetSectionsAsync(Session session, CancellationToken cancellationToken)
        {
            if (_async is not null)
            {
                return await _async(cancellationToken).ConfigureAwait(false);
            }
            return _sync!(session);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  V1.5 (#199): client-side Mermaid rendering
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureAsync_ByDefault_EmitsPinnedMermaidScriptTagWithSri()
    {
        var folder = CreateSessionFolder("abc");
        var sut = CreateSut();
        var htmlPath = await sut.EnsureAsync(BuildSession("abc"));
        var html = await File.ReadAllTextAsync(htmlPath);

        html.Should().Contain("cdn.jsdelivr.net/npm/mermaid@" + SessionDocsService.MermaidVersion,
            "Mermaid must be loaded from the pinned jsDelivr release");
        html.Should().Contain("integrity=\"" + SessionDocsService.MermaidScriptIntegrity + "\"",
            "the loader tag must carry the SRI hash that pairs with the pinned version");
        html.Should().Contain("crossorigin=\"anonymous\"",
            "crossorigin is required for the browser to verify the integrity attribute");
    }

    [Fact]
    public async Task EnsureAsync_ByDefault_EmitsBootstrapperThatPromotesMermaidBlocks()
    {
        var folder = CreateSessionFolder("abc");
        var sut = CreateSut();
        var htmlPath = await sut.EnsureAsync(BuildSession("abc"));
        var html = await File.ReadAllTextAsync(htmlPath);

        html.Should().Contain("pre.mermaid",
            "the bootstrapper must promote the Markdig Diagrams-extension output (pre.mermaid)");
        html.Should().Contain("pre > code.language-mermaid",
            "the bootstrapper must also handle the standard fenced code fallback");
        html.Should().Contain("div.className = 'mermaid'",
            "promoted blocks become div.mermaid elements that mermaid.run() understands");
        html.Should().Contain("startOnLoad: false",
            "the bootstrapper drives mermaid.run() manually after promotion");
        html.Should().Contain("prefers-color-scheme: dark",
            "theme must auto-switch with the OS color scheme");
    }

    [Fact]
    public async Task EnsureAsync_NoMermaidOptOutFile_SuppressesScriptTags()
    {
        var folder = CreateSessionFolder("abc");
        await File.WriteAllBytesAsync(
            Path.Combine(folder, SessionDocsService.NoMermaidOptOutFileName),
            Array.Empty<byte>());

        var sut = CreateSut();
        var htmlPath = await sut.EnsureAsync(BuildSession("abc"));
        var html = await File.ReadAllTextAsync(htmlPath);

        html.Should().NotContain("cdn.jsdelivr.net/npm/mermaid",
            $"presence of {SessionDocsService.NoMermaidOptOutFileName} must drop the CDN load");
        html.Should().NotContain("startOnLoad: false",
            "the bootstrapper script must be suppressed alongside the loader");
    }

    [Fact]
    public async Task EnsureAsync_TogglingNoMermaidOptOut_TriggersRegeneration()
    {
        var folder = CreateSessionFolder("abc");
        var sut = CreateSut();

        var htmlPath = await sut.EnsureAsync(BuildSession("abc"));
        var firstHtml = await File.ReadAllTextAsync(htmlPath);
        firstHtml.Should().Contain("cdn.jsdelivr.net/npm/mermaid");

        await Task.Delay(1100);
        await File.WriteAllBytesAsync(
            Path.Combine(folder, SessionDocsService.NoMermaidOptOutFileName),
            Array.Empty<byte>());

        await sut.EnsureAsync(BuildSession("abc"));
        var secondHtml = await File.ReadAllTextAsync(htmlPath);
        secondHtml.Should().NotContain("cdn.jsdelivr.net/npm/mermaid",
            "adding the opt-out file must invalidate the staleness check and re-render without Mermaid");
    }

    [Fact]
    public async Task EnsureAsync_MarkdownMermaidFence_RendersAsMermaidPreBlock()
    {
        // Sanity check: Markdig with UseAdvancedExtensions() emits the
        // <pre class="mermaid"> shape (via the Diagrams extension) that
        // our bootstrapper expects and promotes.
        var folder = CreateSessionFolder("abc");
        await File.WriteAllTextAsync(
            Path.Combine(folder, SessionDocsService.DocsMarkdownFileName),
            "# Title\n\n```mermaid\nflowchart LR; A --> B\n```\n");

        var sut = CreateSut();
        var htmlPath = await sut.EnsureAsync(BuildSession("abc"));
        var html = await File.ReadAllTextAsync(htmlPath);

        html.Should().Contain("<pre class=\"mermaid\">",
            "Markdig's Diagrams extension must emit the pre.mermaid shape for the bootstrapper");
        html.Should().Contain("flowchart LR",
            "the fence body must be preserved verbatim in the rendered code block");
    }

    [Fact]
    public void MermaidConstants_HavePinnedExpectedShape()
    {
        SessionDocsService.MermaidVersion.Should().MatchRegex(@"^\d+\.\d+\.\d+$",
            "version must be a fully-pinned semver");
        SessionDocsService.MermaidScriptUrl.Should().StartWith("https://cdn.jsdelivr.net/npm/mermaid@");
        SessionDocsService.MermaidScriptUrl.Should().EndWith("/dist/mermaid.min.js",
            "we load the UMD bundle so SRI works against <script src>");
        SessionDocsService.MermaidScriptIntegrity.Should().StartWith("sha384-",
            "SRI hash must be sha384 to match what we ship");
    }
}

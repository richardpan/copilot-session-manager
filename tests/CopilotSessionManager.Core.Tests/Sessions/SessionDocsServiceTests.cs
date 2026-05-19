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
}

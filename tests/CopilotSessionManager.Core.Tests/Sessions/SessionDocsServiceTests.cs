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
}

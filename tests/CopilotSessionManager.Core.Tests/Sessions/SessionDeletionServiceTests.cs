using System;
using System.IO;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Sessions;

/// <summary>
/// Tests for hard-delete (#106). Uses a real temp folder for the session so
/// we can prove the recursive Directory.Delete call wires up properly. The
/// sidecar stores are mocked so we can prove the delete also clears
/// overrides without relying on the on-disk JSON impls.
/// </summary>
public class SessionDeletionServiceTests : IDisposable
{
    private readonly string _root;
    private readonly Mock<ISessionFolderReader> _folders = new();
    private readonly Mock<ISessionDisplayNameStore> _displayNames = new();
    private readonly Mock<ISessionLabelStore> _labels = new();
    private readonly Mock<CopilotSessionManager.Core.GitHub.Storage.ISessionGitHubLinksStore> _github = new();
    private readonly InMemoryRunningSessionRegistry _registry = new();
    private readonly Mock<IDeletedSessionRegistry> _tombstones = new();

    public SessionDeletionServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csm-delete-" + Guid.NewGuid().ToString("N"));
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

    private SessionDeletionService CreateSut() => new(
        _folders.Object,
        _displayNames.Object,
        _labels.Object,
        _github.Object,
        _registry,
        _tombstones.Object,
        NullLogger<SessionDeletionService>.Instance);

    private string CreateSessionFolder(string sessionId, bool withChildren = true)
    {
        var path = Path.Combine(_root, sessionId);
        Directory.CreateDirectory(path);
        if (withChildren)
        {
            Directory.CreateDirectory(Path.Combine(path, "checkpoints"));
            File.WriteAllText(Path.Combine(path, "state.json"), "{}");
            File.WriteAllText(Path.Combine(path, "checkpoints", "001.md"), "# checkpoint 1");
        }
        _folders.Setup(f => f.GetSessionFolderPath(sessionId)).Returns(path);
        return path;
    }

    [Fact]
    public async Task DeleteAsync_RemovesFolderAndAllChildren()
    {
        var path = CreateSessionFolder("abc");

        var result = await CreateSut().DeleteAsync("abc");

        result.Success.Should().BeTrue();
        result.FolderPath.Should().Be(path);
        result.ErrorMessage.Should().BeNull();
        Directory.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_FolderAlreadyGone_ReturnsSuccessAndStillClearsSidecars()
    {
        _folders.Setup(f => f.GetSessionFolderPath("ghost"))
            .Returns(Path.Combine(_root, "ghost-not-here"));

        var result = await CreateSut().DeleteAsync("ghost");

        result.Success.Should().BeTrue();
        _displayNames.Verify(d => d.RemoveAsync("ghost", It.IsAny<System.Threading.CancellationToken>()), Times.Once);
        _labels.Verify(l => l.RemoveAsync("ghost", It.IsAny<System.Threading.CancellationToken>()), Times.Once);
        _github.Verify(g => g.ClearAsync("ghost", It.IsAny<System.Threading.CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_AlsoClearsAllSidecarsOnSuccess()
    {
        CreateSessionFolder("abc");
        _registry.Register("abc", 1234);

        await CreateSut().DeleteAsync("abc");

        _displayNames.Verify(d => d.RemoveAsync("abc", It.IsAny<System.Threading.CancellationToken>()), Times.Once);
        _labels.Verify(l => l.RemoveAsync("abc", It.IsAny<System.Threading.CancellationToken>()), Times.Once);
        _github.Verify(g => g.ClearAsync("abc", It.IsAny<System.Threading.CancellationToken>()), Times.Once);
        _registry.TryGetProcessId("abc").Should().BeNull("the running PID must be unregistered after delete");
    }

    [Fact]
    public async Task DeleteAsync_WithReadOnlyFiles_StillSucceeds()
    {
        var path = CreateSessionFolder("abc");
        var readOnlyFile = Path.Combine(path, "state.json");
        File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);

        var result = await CreateSut().DeleteAsync("abc");

        result.Success.Should().BeTrue();
        Directory.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_SidecarFailures_DoNotPropagate()
    {
        CreateSessionFolder("abc");
        _displayNames.Setup(d => d.RemoveAsync("abc", It.IsAny<System.Threading.CancellationToken>()))
            .ThrowsAsync(new IOException("boom"));
        _labels.Setup(l => l.RemoveAsync("abc", It.IsAny<System.Threading.CancellationToken>()))
            .ThrowsAsync(new IOException("boom"));

        var result = await CreateSut().DeleteAsync("abc");

        result.Success.Should().BeTrue("transient sidecar I/O failures must not poison the delete result");
    }

    [Fact]
    public async Task DeleteAsync_SidecarsAreOptional()
    {
        var path = CreateSessionFolder("abc");
        var sut = new SessionDeletionService(
            _folders.Object,
            displayNames: null, labels: null, githubLinks: null, registry: null,
            NullLogger<SessionDeletionService>.Instance);

        var result = await sut.DeleteAsync("abc");

        result.Success.Should().BeTrue();
        Directory.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_NullOrEmptyId_Throws()
    {
        var sut = CreateSut();
        await Assert.ThrowsAsync<ArgumentException>(() => sut.DeleteAsync(string.Empty));
        await Assert.ThrowsAsync<ArgumentException>(() => sut.DeleteAsync("   "));
    }

    [Fact]
    public async Task DeleteAsync_FolderInUse_ReturnsFailure()
    {
        var path = CreateSessionFolder("abc");
        var lockedFile = Path.Combine(path, "locked.bin");
        File.WriteAllText(lockedFile, "x");

        // Hold an exclusive read+write lock on a file inside the folder so
        // recursive Directory.Delete fails with IOException.
        await using var hold = new FileStream(
            lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await CreateSut().DeleteAsync("abc");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNull();
        Directory.Exists(path).Should().BeTrue("delete should leave the folder intact when it cannot remove it");
    }

    [Fact]
    public async Task DeleteAsync_TombstonesSessionAfterSuccessfulDelete()
    {
        CreateSessionFolder("abc");

        await CreateSut().DeleteAsync("abc");

        // #125: the on-disk row in Copilot CLI's session-store.db is left
        // behind by design (ADR-002). Without the tombstone the next
        // discovery rescan would re-emit a "DB-only" session card and the
        // UI would resurrect what the user just deleted.
        _tombstones.Verify(
            t => t.RecordAsync("abc", It.IsAny<System.Threading.CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_TombstonesEvenWhenFolderIsAlreadyGone()
    {
        // "Already gone" still returns success — and we still need to
        // tombstone, otherwise the user sees the same ghost card on every
        // rescan until they restart Copilot CLI.
        _folders.Setup(f => f.GetSessionFolderPath("ghost"))
            .Returns(Path.Combine(_root, "ghost-not-here"));

        var result = await CreateSut().DeleteAsync("ghost");

        result.Success.Should().BeTrue();
        _tombstones.Verify(
            t => t.RecordAsync("ghost", It.IsAny<System.Threading.CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_TombstoneFailureDoesNotPoisonResult()
    {
        CreateSessionFolder("abc");
        _tombstones.Setup(t => t.RecordAsync("abc", It.IsAny<System.Threading.CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));

        var result = await CreateSut().DeleteAsync("abc");

        result.Success.Should().BeTrue(
            "the folder is gone — failing the delete because the tombstone could not be written would leave the user stuck");
    }

    [Fact]
    public async Task DeleteAsync_FolderInUse_DoesNotTombstone()
    {
        // We must not tombstone when the folder is still on disk —
        // otherwise the next rescan would suppress a session that is
        // still genuinely there.
        var path = CreateSessionFolder("abc");
        var lockedFile = Path.Combine(path, "locked.bin");
        File.WriteAllText(lockedFile, "x");
        await using var hold = new FileStream(
            lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await CreateSut().DeleteAsync("abc");

        _tombstones.Verify(
            t => t.RecordAsync(It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>()),
            Times.Never);
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using CopilotSessionManager.Native;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels;

/// <summary>
/// Tests for the V1.1 SessionCardViewModel surface added by #104, #105, #106.
/// Exercises the open / rename / delete commands directly without spinning
/// up a dispatcher.
/// </summary>
public class SessionCardViewModelV11Tests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);

    private static Session BuildSession(
        string id = "sess-1",
        string? cwd = @"C:\ws\repo",
        string? summary = "Original summary") =>
        new(
            Id: id,
            Cwd: cwd,
            Repository: "owner/repo",
            Branch: "main",
            Summary: summary,
            HostType: "cli",
            CreatedAt: Now.AddMinutes(-10),
            UpdatedAt: Now,
            TurnCount: 1,
            Status: SessionStatus.Idle,
            CopilotVersion: CopilotVersion.Zero,
            Locks: Array.Empty<SessionLockInfo>());

    private static SessionCardViewModel CreateCard(
        Session? session = null,
        ISessionLauncher? launcher = null,
        IRunningSessionRegistry? registry = null,
        IWindowActivator? activator = null,
        ISessionDisplayNameStore? displayNames = null,
        string? displayNameOverride = null,
        ISessionDeletionService? deletion = null,
        Func<SessionDeletionPrompt, bool>? confirm = null,
        Func<string, Task>? onDeleted = null) =>
        new(
            session ?? BuildSession(),
            SessionType.Exploratory,
            new FixedTimeProvider(Now),
            modelCatalog: null, costCalculator: null,
            fileLauncher: null, lockCleanup: null,
            sessionLauncher: launcher, logger: null,
            openMergeWizard: null, issueLinks: null,
            runningSessions: registry, windowActivator: activator,
            displayNameStore: displayNames, displayNameOverride: displayNameOverride,
            deletionService: deletion, confirmDelete: confirm, onDeleted: onDeleted);

    // ---- #105 rename ----

    [Fact]
    public void DisplayName_FallsBackToTitle_WhenNoOverride()
    {
        var sut = CreateCard();
        sut.DisplayName.Should().Be(sut.Title);
        sut.HasDisplayNameOverride.Should().BeFalse();
    }

    [Fact]
    public void DisplayName_UsesOverride_WhenProvided()
    {
        var sut = CreateCard(displayNameOverride: "Pretty name");
        sut.DisplayName.Should().Be("Pretty name");
        sut.HasDisplayNameOverride.Should().BeTrue();
        sut.TitleTooltip.Should().Contain("Original");
    }

    [Fact]
    public void BeginRenameCommand_DisabledWhenStoreIsMissing()
    {
        CreateCard().BeginRenameCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void BeginRenameCommand_PutsCardInEditMode_AndSeedsBuffer()
    {
        var store = new Mock<ISessionDisplayNameStore>();
        var sut = CreateCard(displayNames: store.Object, displayNameOverride: "Pretty name");

        sut.BeginRenameCommand.CanExecute(null).Should().BeTrue();
        sut.BeginRenameCommand.Execute(null);

        sut.IsEditingTitle.Should().BeTrue();
        sut.EditableTitle.Should().Be("Pretty name");
        sut.CommitRenameCommand.CanExecute(null).Should().BeTrue();
        sut.CancelRenameCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task CommitRename_StoresOverride_AndExitsEditMode()
    {
        var store = new Mock<ISessionDisplayNameStore>();
        var sut = CreateCard(displayNames: store.Object);
        sut.BeginRenameCommand.Execute(null);
        sut.EditableTitle = "New label";

        await sut.CommitRenameCommand.ExecuteAsync(null);

        store.Verify(s => s.SetAsync("sess-1", "New label", It.IsAny<CancellationToken>()), Times.Once);
        sut.IsEditingTitle.Should().BeFalse();
        sut.DisplayName.Should().Be("New label");
        sut.HasDisplayNameOverride.Should().BeTrue();
    }

    [Fact]
    public async Task CommitRename_TrimsWhitespace()
    {
        var store = new Mock<ISessionDisplayNameStore>();
        var sut = CreateCard(displayNames: store.Object);
        sut.BeginRenameCommand.Execute(null);
        sut.EditableTitle = "   spaced   ";

        await sut.CommitRenameCommand.ExecuteAsync(null);

        store.Verify(s => s.SetAsync("sess-1", "spaced", It.IsAny<CancellationToken>()), Times.Once);
        sut.DisplayName.Should().Be("spaced");
    }

    [Fact]
    public async Task CommitRename_Empty_ClearsOverride()
    {
        var store = new Mock<ISessionDisplayNameStore>();
        var sut = CreateCard(displayNames: store.Object, displayNameOverride: "Old name");
        sut.BeginRenameCommand.Execute(null);
        sut.EditableTitle = "   ";

        await sut.CommitRenameCommand.ExecuteAsync(null);

        store.Verify(s => s.RemoveAsync("sess-1", It.IsAny<CancellationToken>()), Times.Once);
        sut.HasDisplayNameOverride.Should().BeFalse();
        sut.DisplayName.Should().Be(sut.Title);
    }

    [Fact]
    public async Task CommitRename_SameAsOriginal_ClearsOverride()
    {
        var store = new Mock<ISessionDisplayNameStore>();
        var sut = CreateCard(displayNames: store.Object, displayNameOverride: "Custom");
        var original = sut.Title;
        sut.BeginRenameCommand.Execute(null);
        sut.EditableTitle = original;

        await sut.CommitRenameCommand.ExecuteAsync(null);

        store.Verify(s => s.RemoveAsync("sess-1", It.IsAny<CancellationToken>()), Times.Once,
            "typing the original name back is the natural way to revert");
        sut.HasDisplayNameOverride.Should().BeFalse();
    }

    [Fact]
    public void CancelRename_DiscardsBufferAndExitsEditMode()
    {
        var store = new Mock<ISessionDisplayNameStore>();
        var sut = CreateCard(displayNames: store.Object, displayNameOverride: "Original");
        sut.BeginRenameCommand.Execute(null);
        sut.EditableTitle = "scratch";

        sut.CancelRenameCommand.Execute(null);

        sut.IsEditingTitle.Should().BeFalse();
        sut.DisplayName.Should().Be("Original", "cancel must not commit anything");
        store.Verify(s => s.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(s => s.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void ApplyDisplayNameOverride_UpdatesBoundProperties()
    {
        var sut = CreateCard();

        sut.ApplyDisplayNameOverride("From elsewhere");

        sut.DisplayName.Should().Be("From elsewhere");
        sut.HasDisplayNameOverride.Should().BeTrue();
    }

    // ---- #106 delete ----

    [Fact]
    public void DeleteCommand_DisabledWithoutServices()
    {
        CreateCard().DeleteCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteCommand_RespectsCancelFromConfirmCallback()
    {
        var deletion = new Mock<ISessionDeletionService>();
        var deleted = false;
        Func<string, Task> onDeleted = _ => { deleted = true; return Task.CompletedTask; };
        var sut = CreateCard(deletion: deletion.Object, confirm: _ => false, onDeleted: onDeleted);

        await sut.DeleteCommand.ExecuteAsync(null);

        deletion.Verify(d => d.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        deleted.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteCommand_OnSuccess_FiresOnDeletedCallback()
    {
        var deletion = new Mock<ISessionDeletionService>();
        deletion.Setup(d => d.DeleteAsync("sess-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SessionDeletionResult.Ok(@"C:\fake\sess-1"));
        string? deletedId = null;
        Func<string, Task> onDeleted = id => { deletedId = id; return Task.CompletedTask; };
        var sut = CreateCard(deletion: deletion.Object, confirm: _ => true, onDeleted: onDeleted);

        await sut.DeleteCommand.ExecuteAsync(null);

        deletedId.Should().Be("sess-1");
        sut.LastActionMessage.Should().Contain("deleted");
    }

    [Fact]
    public async Task DeleteCommand_OnFailure_DoesNotFireOnDeleted_AndSurfacesMessage()
    {
        var deletion = new Mock<ISessionDeletionService>();
        deletion.Setup(d => d.DeleteAsync("sess-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SessionDeletionResult.Failed(@"C:\fake\sess-1", "Locked, try again."));
        var deleted = false;
        Func<string, Task> onDeleted = _ => { deleted = true; return Task.CompletedTask; };
        var sut = CreateCard(deletion: deletion.Object, confirm: _ => true, onDeleted: onDeleted);

        await sut.DeleteCommand.ExecuteAsync(null);

        deleted.Should().BeFalse();
        sut.LastActionMessage.Should().Be("Locked, try again.");
    }

    [Fact]
    public async Task DeleteCommand_PromptCarriesDisplayName_NotRawTitle()
    {
        SessionDeletionPrompt? captured = null;
        var deletion = new Mock<ISessionDeletionService>();
        deletion.Setup(d => d.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SessionDeletionResult.Ok(@"C:\fake"));
        var sut = CreateCard(deletion: deletion.Object, displayNameOverride: "My nickname",
            confirm: prompt => { captured = prompt; return false; });

        await sut.DeleteCommand.ExecuteAsync(null);

        captured.Should().NotBeNull();
        captured!.SessionId.Should().Be("sess-1");
        captured.DisplayName.Should().Be("My nickname");
    }

    // ---- #104 open ----

    [Fact]
    public void OpenCommand_RequiresLauncher()
    {
        CreateCard().OpenCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task OpenCommand_NoTrackedPid_LaunchesFresh_AndRegistersPid()
    {
        var launcher = new RecordingLauncher();
        var registry = new InMemoryRunningSessionRegistry();
        var activator = new Mock<IWindowActivator>();
        var sut = CreateCard(launcher: launcher, registry: registry, activator: activator.Object);

        await sut.OpenCommand.ExecuteAsync(null);

        launcher.Calls.Should().ContainSingle();
        registry.TryGetProcessId("sess-1").Should().Be(99);
        activator.Verify(a => a.Activate(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task OpenCommand_WithLiveTrackedPid_ActivatesWindow_DoesNotRelaunch()
    {
        var launcher = new RecordingLauncher();
        var registry = new InMemoryRunningSessionRegistry();
        registry.Register("sess-1", 4242);
        var activator = new Mock<IWindowActivator>();
        activator.Setup(a => a.Activate(4242)).Returns(WindowActivationResult.Activated);
        var sut = CreateCard(launcher: launcher, registry: registry, activator: activator.Object);

        await sut.OpenCommand.ExecuteAsync(null);

        activator.Verify(a => a.Activate(4242), Times.Once);
        launcher.Calls.Should().BeEmpty("activating an existing window must not spawn a duplicate");
        sut.LastActionMessage.Should().Contain("Brought");
    }

    [Fact]
    public async Task OpenCommand_TrackedPidGone_FallsBackToFreshLaunch()
    {
        var launcher = new RecordingLauncher();
        var registry = new InMemoryRunningSessionRegistry();
        registry.Register("sess-1", 4242);
        var activator = new Mock<IWindowActivator>();
        activator.Setup(a => a.Activate(4242)).Returns(WindowActivationResult.ProcessNotRunning);
        var sut = CreateCard(launcher: launcher, registry: registry, activator: activator.Object);

        await sut.OpenCommand.ExecuteAsync(null);

        activator.Verify(a => a.Activate(4242), Times.Once);
        launcher.Calls.Should().ContainSingle("a dead PID should be replaced with a fresh launch");
        registry.TryGetProcessId("sess-1").Should().Be(99, "the new PID should overwrite the dead one");
    }

    [Fact]
    public async Task OpenCommand_Win32Failure_DoesNotRelaunch()
    {
        var launcher = new RecordingLauncher();
        var registry = new InMemoryRunningSessionRegistry();
        registry.Register("sess-1", 4242);
        var activator = new Mock<IWindowActivator>();
        activator.Setup(a => a.Activate(4242)).Returns(WindowActivationResult.Win32Failure);
        var sut = CreateCard(launcher: launcher, registry: registry, activator: activator.Object);

        await sut.OpenCommand.ExecuteAsync(null);

        launcher.Calls.Should().BeEmpty(
            "Win32 refused focus theft but the window flashed; relaunching would create a duplicate");
        sut.LastActionMessage.Should().Contain("flashed");
    }

    private sealed class RecordingLauncher : ISessionLauncher
    {
        public List<(string sessionId, string? cwd)> Calls { get; } = new();
        public Task<SessionLaunchResult> LaunchAsync(string sessionId, string? workingDirectory = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((sessionId, workingDirectory));
            return Task.FromResult(new SessionLaunchResult(99, "pwsh.exe", "copilot --resume " + sessionId, workingDirectory ?? ""));
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}

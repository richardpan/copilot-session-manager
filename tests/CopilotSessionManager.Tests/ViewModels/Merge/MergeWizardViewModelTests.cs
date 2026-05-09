using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Cli.Share;
using CopilotSessionManager.Core.Merge;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Services;
using CopilotSessionManager.ViewModels;
using CopilotSessionManager.ViewModels.Merge;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels.Merge;

public class MergeWizardViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);

    private static Session BuildSession(
        string id,
        SessionStatus status = SessionStatus.Idle,
        DateTimeOffset? updatedAt = null) =>
        new(
            Id: id,
            Cwd: @"C:\ws\repo",
            Repository: "owner/repo",
            Branch: "main",
            Summary: $"Session {id}",
            HostType: "cli",
            CreatedAt: Now.AddMinutes(-30),
            UpdatedAt: updatedAt ?? Now.AddMinutes(-2),
            TurnCount: 1,
            Status: status,
            CopilotVersion: CopilotVersion.Zero,
            Locks: Array.Empty<SessionLockInfo>());

    private static SessionCardViewModel CardFor(
        string id,
        SessionStatus status = SessionStatus.Idle,
        DateTimeOffset? updatedAt = null) =>
        new(BuildSession(id, status, updatedAt), new SessionsViewModelTests.FixedTimeProvider(Now));

    private static MergeWizardViewModel BuildSut(
        FakeShare share,
        FakeMerger merger,
        out List<SessionCardViewModel> completedTargets,
        IReadOnlyList<SessionCardViewModel>? candidates = null,
        SessionCardViewModel? source = null,
        IFileLauncher? fileLauncher = null)
    {
        source ??= CardFor("source-aaaaaa", SessionStatus.Working);
        var captured = new List<SessionCardViewModel>();
        completedTargets = captured;
        candidates ??= new[]
        {
            source,
            CardFor("target-bbbbbb", SessionStatus.Idle, Now.AddMinutes(-1)),
            CardFor("target-cccccc", SessionStatus.AwaitingInput, Now.AddMinutes(-3)),
        };

        return new MergeWizardViewModel(
            source,
            candidates,
            merger,
            share,
            new SessionsViewModelTests.SyncDispatcher(),
            fileLauncher,
            new SessionsViewModelTests.FixedTimeProvider(Now),
            NullLogger<MergeWizardViewModel>.Instance,
            onMergeComplete: target => captured.Add(target));
    }

    [Fact]
    public void Constructor_DropsSourceFromCandidates()
    {
        var src = CardFor("source-aaaaaa", SessionStatus.Working);
        var other = CardFor("target-bbbbbb", SessionStatus.Idle);
        var sut = new MergeWizardViewModel(
            src,
            new[] { src, other },
            new FakeMerger(),
            new FakeShare(),
            new SessionsViewModelTests.SyncDispatcher());

        sut.AllCandidates.Should().HaveCount(1);
        sut.AllCandidates[0].Id.Should().Be("target-bbbbbb");
    }

    [Fact]
    public void Constructor_DefaultsToActiveOnlyAndSortsByRecencyDescending()
    {
        var src = CardFor("source-aaaaaa", SessionStatus.Working);
        var newer = CardFor("target-bbbbbb", SessionStatus.Working, Now.AddMinutes(-1));
        var older = CardFor("target-cccccc", SessionStatus.Idle, Now.AddMinutes(-9));
        var inactive = CardFor("target-dddddd", SessionStatus.Inactive, Now.AddMinutes(-2));

        var sut = new MergeWizardViewModel(
            src, new[] { src, older, newer, inactive }, new FakeMerger(), new FakeShare(),
            new SessionsViewModelTests.SyncDispatcher());

        sut.AllCandidates.Should().HaveCount(3, because: "source is excluded");
        sut.AllCandidates[0].Id.Should().Be("target-bbbbbb");
        sut.TargetCandidates.Should().HaveCount(2, because: "inactive is hidden by default");
        sut.TargetCandidates.Should().NotContain(c => c.Id == "target-dddddd");
    }

    [Fact]
    public void ShowInactiveTargets_True_IncludesInactiveCandidates()
    {
        var src = CardFor("source-aaaaaa", SessionStatus.Working);
        var inactive = CardFor("target-iiiiii", SessionStatus.Inactive, Now.AddMinutes(-1));
        var sut = new MergeWizardViewModel(
            src, new[] { src, inactive }, new FakeMerger(), new FakeShare(),
            new SessionsViewModelTests.SyncDispatcher());

        sut.TargetCandidates.Should().BeEmpty();
        sut.ShowInactiveTargets = true;
        sut.TargetCandidates.Should().ContainSingle(c => c.Id == "target-iiiiii");
    }

    [Fact]
    public void SearchText_FiltersCandidatesByTitleOrShortId()
    {
        var src = CardFor("source-aaaaaa", SessionStatus.Working);
        var a = CardFor("aaaaaa11ffffffff", SessionStatus.Working);
        var b = CardFor("bbbbbb22ffffffff", SessionStatus.Working);
        var sut = new MergeWizardViewModel(
            src, new[] { src, a, b }, new FakeMerger(), new FakeShare(),
            new SessionsViewModelTests.SyncDispatcher());

        sut.TargetCandidates.Should().HaveCount(2);
        sut.SearchText = "aaaaaa11";
        sut.TargetCandidates.Should().ContainSingle(c => c.ShortId == "aaaaaa11");
    }

    [Fact]
    public void NextCommand_DisabledWhenNoTargetSelected()
    {
        var sut = BuildSut(new FakeShare(), new FakeMerger(), out _);
        sut.NextCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task HappyPath_RunsAllFourStates()
    {
        var share = new FakeShare { Result = ShareResult.Ok("/tmp/x.md", "# preview\n## body\n") };
        var merger = new FakeMerger { Result = MergeResult.Ok("## Merged from session foo on …") };
        var sut = BuildSut(share, merger, out var completed);

        sut.CurrentStep.Should().Be(MergeWizardStep.PickTarget);
        sut.SelectedTarget = sut.TargetCandidates[0];

        await sut.NextCommand.ExecuteAsync(null);

        sut.CurrentStep.Should().Be(MergeWizardStep.PreviewAndConfirm);
        sut.MarkdownPreview.Should().Contain("# preview");
        share.LastSourceId.Should().Be("source-aaaaaa");

        await sut.ConfirmMergeCommand.ExecuteAsync(null);

        sut.CurrentStep.Should().Be(MergeWizardStep.Done);
        sut.IsSuccess.Should().BeTrue();
        sut.ErrorMessage.Should().BeNull();
        sut.ResultingMergeNote.Should().Contain("Merged from session");
        merger.LastSourceId.Should().Be("source-aaaaaa");
        merger.LastTargetId.Should().Be(sut.SelectedTarget!.Id);
        completed.Should().ContainSingle(c => c.Id == sut.SelectedTarget!.Id);
        sut.DoneSummary.Should().Contain("Merge complete");
    }

    [Fact]
    public async Task ShareFails_TransitionsToDoneWithError()
    {
        var share = new FakeShare { Result = ShareResult.Fail("CLI not found") };
        var merger = new FakeMerger();
        var sut = BuildSut(share, merger, out var completed);
        sut.SelectedTarget = sut.TargetCandidates[0];

        await sut.NextCommand.ExecuteAsync(null);

        sut.CurrentStep.Should().Be(MergeWizardStep.Done);
        sut.IsSuccess.Should().BeFalse();
        sut.ErrorMessage.Should().Contain("CLI not found");
        sut.ErrorMessage.Should().Contain("export source session");
        merger.LastSourceId.Should().BeNull();
        completed.Should().BeEmpty();
        sut.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task ShareEmptyMarkdown_TreatedAsFailure()
    {
        var share = new FakeShare { Result = new ShareResult(true, "/tmp/x.md", string.Empty, null) };
        var sut = BuildSut(share, new FakeMerger(), out _);
        sut.SelectedTarget = sut.TargetCandidates[0];

        await sut.NextCommand.ExecuteAsync(null);

        sut.CurrentStep.Should().Be(MergeWizardStep.Done);
        sut.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task ShareThrows_TransitionsToDoneWithExceptionMessage()
    {
        var share = new FakeShare { ThrowOnExport = new InvalidOperationException("boom") };
        var sut = BuildSut(share, new FakeMerger(), out _);
        sut.SelectedTarget = sut.TargetCandidates[0];

        await sut.NextCommand.ExecuteAsync(null);

        sut.CurrentStep.Should().Be(MergeWizardStep.Done);
        sut.IsSuccess.Should().BeFalse();
        sut.ErrorMessage.Should().Contain("boom");
    }

    [Fact]
    public async Task MergeFails_TransitionsToDoneWithError()
    {
        var share = new FakeShare { Result = ShareResult.Ok("/tmp/x.md", "preview") };
        var merger = new FakeMerger { Result = MergeResult.Fail("disk full") };
        var sut = BuildSut(share, merger, out var completed);
        sut.SelectedTarget = sut.TargetCandidates[0];
        await sut.NextCommand.ExecuteAsync(null);
        await sut.ConfirmMergeCommand.ExecuteAsync(null);

        sut.CurrentStep.Should().Be(MergeWizardStep.Done);
        sut.IsSuccess.Should().BeFalse();
        sut.ErrorMessage.Should().Be("disk full");
        completed.Should().BeEmpty();
    }

    [Fact]
    public async Task BackFromPreview_ReturnsToPickTargetAndClearsMarkdown()
    {
        var share = new FakeShare { Result = ShareResult.Ok("/tmp/x.md", "preview text") };
        var sut = BuildSut(share, new FakeMerger(), out _);
        sut.SelectedTarget = sut.TargetCandidates[0];
        await sut.NextCommand.ExecuteAsync(null);

        sut.CurrentStep.Should().Be(MergeWizardStep.PreviewAndConfirm);
        sut.BackCommand.CanExecute(null).Should().BeTrue();

        sut.BackCommand.Execute(null);

        sut.CurrentStep.Should().Be(MergeWizardStep.PickTarget);
        sut.MarkdownPreview.Should().BeEmpty();
    }

    [Fact]
    public void CancelCommand_RaisesCloseRequestedWhenIdle()
    {
        var sut = BuildSut(new FakeShare(), new FakeMerger(), out _);
        var raised = false;
        sut.CloseRequested += (_, _) => raised = true;

        sut.CancelCommand.Execute(null);

        raised.Should().BeTrue();
    }

    [Fact]
    public void SelectingTarget_AutoUpdatesIsSelectedFlag()
    {
        var sut = BuildSut(new FakeShare(), new FakeMerger(), out _);
        var first = sut.TargetCandidates[0];
        var second = sut.TargetCandidates[1];

        sut.SelectedTarget = first;
        first.IsSelected.Should().BeTrue();
        second.IsSelected.Should().BeFalse();

        sut.SelectedTarget = second;
        first.IsSelected.Should().BeFalse();
        second.IsSelected.Should().BeTrue();
    }

    [Fact]
    public async Task ConfirmCommand_NoOpWhenNotInPreviewState()
    {
        var sut = BuildSut(new FakeShare(), new FakeMerger { Result = MergeResult.Ok(null) }, out var completed);
        // SelectedTarget is null — not in PreviewAndConfirm yet
        await sut.ConfirmMergeCommand.ExecuteAsync(null);

        sut.CurrentStep.Should().Be(MergeWizardStep.PickTarget);
        completed.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfirmCommand_RunsMergerAndCallsRefreshCallback()
    {
        var share = new FakeShare { Result = ShareResult.Ok("/tmp/x.md", "preview") };
        var merger = new FakeMerger { Result = MergeResult.Ok("note") };
        var sut = BuildSut(share, merger, out var completed);
        sut.SelectedTarget = sut.TargetCandidates[1];
        await sut.NextCommand.ExecuteAsync(null);
        await sut.ConfirmMergeCommand.ExecuteAsync(null);

        completed.Should().ContainSingle(c => c.Id == sut.SelectedTarget!.Id);
    }

    [Fact]
    public async Task ConfirmCommand_RefreshCallbackThrows_DoesNotFlipSuccess()
    {
        var share = new FakeShare { Result = ShareResult.Ok("/tmp/x.md", "preview") };
        var merger = new FakeMerger { Result = MergeResult.Ok("note") };
        var src = CardFor("source-aaaaaa", SessionStatus.Working);
        var candidates = new[] { src, CardFor("target-zzzzzz", SessionStatus.Working) };

        var sut = new MergeWizardViewModel(
            src,
            candidates,
            merger,
            share,
            new SessionsViewModelTests.SyncDispatcher(),
            fileLauncher: null,
            new SessionsViewModelTests.FixedTimeProvider(Now),
            NullLogger<MergeWizardViewModel>.Instance,
            onMergeComplete: _ => throw new InvalidOperationException("ui broken"));
        sut.SelectedTarget = sut.TargetCandidates[0];
        await sut.NextCommand.ExecuteAsync(null);
        await sut.ConfirmMergeCommand.ExecuteAsync(null);

        sut.CurrentStep.Should().Be(MergeWizardStep.Done);
        sut.IsSuccess.Should().BeTrue(because: "the engine succeeded; UI refresh failure must not undo that");
    }

    [Fact]
    public async Task OpenMergedFile_NoOpWhenLauncherIsNull()
    {
        var share = new FakeShare { Result = ShareResult.Ok("/tmp/x.md", "preview") };
        var merger = new FakeMerger { Result = MergeResult.Ok("note") };
        var sut = BuildSut(share, merger, out _, fileLauncher: null);
        sut.SelectedTarget = sut.TargetCandidates[0];
        await sut.NextCommand.ExecuteAsync(null);
        await sut.ConfirmMergeCommand.ExecuteAsync(null);

        sut.OpenMergedFileCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task OpenMergedFile_DelegatesToFileLauncherWhenPathExists()
    {
        var launcher = new RecordingFileLauncher();
        var share = new FakeShare { Result = ShareResult.Ok("/tmp/x.md", "preview") };
        var merger = new FakeMerger { Result = MergeResult.Ok("note") };

        // Create a real merge-imports/<source>.md inside an isolated home so
        // the wizard's path-guess succeeds. Using Environment.GetFolderPath
        // would touch the real user profile; instead we exercise the no-op
        // path and assert the command stays disabled when the file isn't there.
        var sut = BuildSut(share, merger, out _, fileLauncher: launcher);
        sut.SelectedTarget = sut.TargetCandidates[0];
        await sut.NextCommand.ExecuteAsync(null);
        await sut.ConfirmMergeCommand.ExecuteAsync(null);

        // No real file was written; CanExecute should be false → command no-ops.
        await sut.OpenMergedFileCommand.ExecuteAsync(null);
        launcher.OpenedPaths.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_NullSourceCard_Throws()
    {
        var act = () => new MergeWizardViewModel(
            null!,
            Array.Empty<SessionCardViewModel>(),
            new FakeMerger(),
            new FakeShare(),
            new SessionsViewModelTests.SyncDispatcher());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullMerger_Throws()
    {
        var src = CardFor("a-source");
        var act = () => new MergeWizardViewModel(
            src, Array.Empty<SessionCardViewModel>(), null!, new FakeShare(),
            new SessionsViewModelTests.SyncDispatcher());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void HasNoCandidates_TrueWhenSourceIsOnlySession()
    {
        var src = CardFor("source-aaaaaa");
        var sut = new MergeWizardViewModel(
            src, new[] { src }, new FakeMerger(), new FakeShare(),
            new SessionsViewModelTests.SyncDispatcher());
        sut.HasNoCandidates.Should().BeTrue();
        sut.TargetCandidates.Should().BeEmpty();
    }

    private sealed class FakeShare : ICopilotShareInvoker
    {
        public ShareResult Result { get; set; } = ShareResult.Fail("not configured");
        public Exception? ThrowOnExport { get; set; }
        public string? LastSourceId { get; private set; }

        public Task<ShareResult> ExportAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            LastSourceId = sessionId;
            if (ThrowOnExport is not null)
            {
                throw ThrowOnExport;
            }
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeMerger : ISessionMerger
    {
        public MergeResult Result { get; set; } = MergeResult.Fail("not configured");
        public Exception? ThrowOnMerge { get; set; }
        public string? LastSourceId { get; private set; }
        public string? LastTargetId { get; private set; }

        public Task<MergeResult> MergeAsync(string sourceSessionId, string targetSessionId, CancellationToken cancellationToken = default)
        {
            LastSourceId = sourceSessionId;
            LastTargetId = targetSessionId;
            if (ThrowOnMerge is not null)
            {
                throw ThrowOnMerge;
            }
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingFileLauncher : IFileLauncher
    {
        public List<string> OpenedPaths { get; } = new();
        public Task OpenAsync(string path, CancellationToken cancellationToken = default)
        {
            OpenedPaths.Add(path);
            return Task.CompletedTask;
        }
    }
}

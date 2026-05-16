using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels;

/// <summary>
/// V1.4 (#159 Phase 6B) tests covering the rewired
/// <see cref="SessionCardViewModel.OpenCommand"/> (embedded-first) and the
/// new <see cref="SessionCardViewModel.OpenInExternalCommand"/> secondary
/// affordance.
/// </summary>
public class SessionCardViewModelOpenEmbeddedTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static Session BuildSession() => new(
        Id: "sess-159",
        Cwd: @"C:\ws\repo",
        Repository: "owner/repo",
        Branch: "main",
        Summary: "embedded routing test",
        HostType: "cli",
        CreatedAt: Now.AddMinutes(-10),
        UpdatedAt: Now,
        TurnCount: 1,
        Status: SessionStatus.Idle,
        CopilotVersion: CopilotVersion.Zero,
        Locks: Array.Empty<SessionLockInfo>());

    private static SessionCardViewModel CreateCard(
        ISessionLauncher? launcher,
        Action<SessionCardViewModel>? openEmbeddedTerminal) =>
        new(
            model: BuildSession(),
            label: SessionType.Exploratory,
            timeProvider: TimeProvider.System,
            modelCatalog: null,
            costCalculator: null,
            fileLauncher: null,
            lockCleanup: null,
            sessionLauncher: launcher,
            logger: null,
            openMergeWizard: null,
            issueLinks: null,
            runningSessions: null,
            windowActivator: null,
            displayNameStore: null,
            displayNameOverride: null,
            deletionService: null,
            confirmDelete: null,
            starStore: null,
            isStarred: false,
            onDeleted: null,
            docFreshness: null,
            readmeService: null,
            wrapUpStateStore: null,
            clipboardService: null,
            appSettings: null,
            openEmbeddedTerminal: openEmbeddedTerminal);

    [Fact]
    public async Task OpenCommand_invokes_embedded_callback_when_wired_and_skips_launcher()
    {
        var launcher = new RecordingLauncher();
        SessionCardViewModel? captured = null;

        var card = CreateCard(launcher, c => captured = c);
        await card.OpenCommand.ExecuteAsync(null);

        captured.Should().BeSameAs(card);
        launcher.Calls.Should().BeEmpty();
        card.LastActionMessage.Should().Be("Opened embedded terminal tab.");
    }

    [Fact]
    public async Task OpenCommand_falls_back_to_launcher_when_embedded_callback_is_null()
    {
        var launcher = new RecordingLauncher();
        var card = CreateCard(launcher, openEmbeddedTerminal: null);

        await card.OpenCommand.ExecuteAsync(null);

        launcher.Calls.Should().ContainSingle().Which.sessionId.Should().Be("sess-159");
        card.LastActionMessage.Should().Be("Launched PowerShell (pid 99).");
    }

    [Fact]
    public async Task OpenInExternalCommand_always_uses_launcher_even_when_embedded_callback_is_wired()
    {
        var launcher = new RecordingLauncher();
        var embeddedInvoked = false;
        var card = CreateCard(launcher, _ => embeddedInvoked = true);

        await card.OpenInExternalCommand.ExecuteAsync(null);

        embeddedInvoked.Should().BeFalse();
        launcher.Calls.Should().ContainSingle().Which.sessionId.Should().Be("sess-159");
        card.LastActionMessage.Should().Be("Launched PowerShell (pid 99).");
    }

    [Fact]
    public void CanOpen_is_true_when_only_embedded_callback_is_wired()
    {
        var card = CreateCard(launcher: null, openEmbeddedTerminal: _ => { });

        card.OpenCommand.CanExecute(null).Should().BeTrue();
        card.OpenInExternalCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void CanOpen_is_true_when_only_launcher_is_wired()
    {
        var card = CreateCard(launcher: new RecordingLauncher(), openEmbeddedTerminal: null);

        card.OpenCommand.CanExecute(null).Should().BeTrue();
        card.OpenInExternalCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void CanOpen_is_false_when_neither_is_wired()
    {
        var card = CreateCard(launcher: null, openEmbeddedTerminal: null);

        card.OpenCommand.CanExecute(null).Should().BeFalse();
        card.OpenInExternalCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task OpenCommand_swallows_embedded_callback_exceptions_and_reports_via_LastActionMessage()
    {
        var card = CreateCard(
            launcher: null,
            openEmbeddedTerminal: _ => throw new InvalidOperationException("kaboom"));

        await card.OpenCommand.ExecuteAsync(null);

        card.LastActionMessage.Should().Be("Open failed: kaboom");
    }

    private sealed class RecordingLauncher : ISessionLauncher
    {
        public List<(string sessionId, string? cwd)> Calls { get; } = new();

        public Task<SessionLaunchResult> LaunchAsync(string sessionId, string? workingDirectory = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((sessionId, workingDirectory));
            return Task.FromResult(new SessionLaunchResult(99, "pwsh.exe", "copilot --resume " + sessionId, workingDirectory ?? string.Empty));
        }

        public Task<SessionLaunchResult> LaunchNewAsync(string? workingDirectory = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new SessionLaunchResult(100, "pwsh.exe", "copilot", workingDirectory ?? string.Empty));
    }
}

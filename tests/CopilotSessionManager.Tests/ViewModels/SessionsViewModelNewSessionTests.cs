using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels;

/// <summary>
/// Coverage for the V1.2 "+ New session" command (#108) on
/// <see cref="SessionsViewModel"/>. Verifies CanExecute gating, the happy
/// path (status messaging), and a launcher that throws.
/// </summary>
public class SessionsViewModelNewSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 11, 12, 0, 0, TimeSpan.Zero);

    private static Session BuildSession(string id) =>
        new(
            Id: id,
            Cwd: @"C:\ws\repo",
            Repository: "owner/repo",
            Branch: "main",
            Summary: "test",
            HostType: "cli",
            CreatedAt: Now.AddMinutes(-30),
            UpdatedAt: Now.AddMinutes(-2),
            TurnCount: 1,
            Status: SessionStatus.Idle,
            CopilotVersion: CopilotVersion.Zero,
            Locks: Array.Empty<SessionLockInfo>());

    private static SessionsViewModel CreateSut(ISessionLauncher? launcher)
    {
        var tp = new SessionsViewModelTests.FixedTimeProvider(Now);
        var disc = new SessionsViewModelTests.FakeDiscoveryService(new[] { BuildSession("s1") });
        var labels = new SessionsViewModelTests.FakeLabelStore();
        var readme = new SessionsViewModelTests.FakeReadmeService();
        var fileLauncher = new SessionsViewModelTests.FakeFileLauncher();
        var vm = new SessionsViewModel(
            disc, labels, readme, fileLauncher,
            new SessionsViewModelTests.SyncDispatcher(), tp,
            modelCatalog: null, costCalculator: null, githubClient: null,
            lockCleanup: null, sessionLauncher: launcher, loggerFactory: null,
            NullLogger<SessionsViewModel>.Instance);
        vm.InitializeAsync().GetAwaiter().GetResult();
        return vm;
    }

    [Fact]
    public void NewSessionCommand_DisabledWhenLauncherMissing()
    {
        var vm = CreateSut(launcher: null);
        vm.NewSessionCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void NewSessionCommand_EnabledWhenLauncherWired()
    {
        var vm = CreateSut(new RecordingLauncher());
        vm.NewSessionCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task NewSessionAsync_NoLauncher_DoesNothing()
    {
        var vm = CreateSut(launcher: null);
        var before = vm.StatusMessage;
        await vm.NewSessionAsync();
        vm.StatusMessage.Should().Be(before, "no launcher means no-op");
    }

    [Fact]
    public async Task NewSessionAsync_HappyPath_InvokesLauncherAndPostsStatus()
    {
        var launcher = new RecordingLauncher();
        var vm = CreateSut(launcher);

        await vm.NewSessionAsync();

        launcher.NewCalls.Should().Be(1, "the launcher should be invoked exactly once");
        launcher.LastNewCwd.Should().BeNull("CSM defaults the cwd to the user's home (the launcher resolves null)");
        vm.StatusMessage.Should().Contain("Launched new Copilot session");
        vm.StatusMessage.Should().Contain("4242");
    }

    [Fact]
    public async Task NewSessionAsync_LauncherThrows_PostsFriendlyError()
    {
        var launcher = new ThrowingLauncher("pwsh missing");
        var vm = CreateSut(launcher);

        await vm.NewSessionAsync();

        vm.StatusMessage.Should().Contain("Could not launch new session");
        vm.StatusMessage.Should().Contain("pwsh missing");
    }

    [Fact]
    public async Task NewSessionAsync_PrefersEmbeddedCallback_WhenWired()
    {
        var launcher = new RecordingLauncher();
        var vm = CreateSut(launcher);
        var embeddedCalls = 0;
        vm.SetOpenNewEmbeddedCopilotTabCallback(() => embeddedCalls++);

        await vm.NewSessionAsync();

        embeddedCalls.Should().Be(1, "embedded route is the new default when a callback is wired");
        launcher.NewCalls.Should().Be(0, "external launcher must not be invoked when the embedded callback handles the request");
        vm.StatusMessage.Should().Contain("embedded tab");
    }

    [Fact]
    public async Task NewSessionAsync_FallsBackToExternal_WhenNoEmbeddedCallback()
    {
        var launcher = new RecordingLauncher();
        var vm = CreateSut(launcher);

        await vm.NewSessionAsync();

        launcher.NewCalls.Should().Be(1, "external launcher remains the fallback when no embedded callback is wired");
        vm.StatusMessage.Should().Contain("Launched new Copilot session");
    }

    [Fact]
    public async Task NewSessionExternalAsync_AlwaysInvokesExternalLauncher_EvenWithEmbeddedWired()
    {
        var launcher = new RecordingLauncher();
        var vm = CreateSut(launcher);
        var embeddedCalls = 0;
        vm.SetOpenNewEmbeddedCopilotTabCallback(() => embeddedCalls++);

        await vm.NewSessionExternalAsync();

        embeddedCalls.Should().Be(0, "the external command must bypass the embedded callback");
        launcher.NewCalls.Should().Be(1);
        vm.StatusMessage.Should().Contain("Launched new Copilot session");
    }

    [Fact]
    public async Task NewSessionAsync_EmbeddedCallbackThrows_PostsFriendlyError()
    {
        var launcher = new RecordingLauncher();
        var vm = CreateSut(launcher);
        vm.SetOpenNewEmbeddedCopilotTabCallback(() => throw new InvalidOperationException("tabs not ready"));

        await vm.NewSessionAsync();

        vm.StatusMessage.Should().Contain("Could not open embedded session");
        vm.StatusMessage.Should().Contain("tabs not ready");
        launcher.NewCalls.Should().Be(0, "embedded callback failure must not silently fall through to the external launcher");
    }

    [Fact]
    public void NewSessionCommand_EnabledWithEmbeddedCallback_EvenWithoutLauncher()
    {
        var vm = CreateSut(launcher: null);
        vm.NewSessionCommand.CanExecute(null).Should().BeFalse("baseline: no launcher and no embedded callback");

        vm.SetOpenNewEmbeddedCopilotTabCallback(() => { });

        vm.NewSessionCommand.CanExecute(null).Should().BeTrue("embedded callback alone is enough");
    }

    [Fact]
    public void NewSessionExternalCommand_DisabledWhenLauncherMissing()
    {
        var vm = CreateSut(launcher: null);
        vm.SetOpenNewEmbeddedCopilotTabCallback(() => { });

        vm.NewSessionExternalCommand.CanExecute(null).Should().BeFalse(
            "the external entry never falls back to the embedded callback");
    }

    private sealed class RecordingLauncher : ISessionLauncher
    {
        public int NewCalls { get; private set; }
        public string? LastNewCwd { get; private set; }
        public Task<SessionLaunchResult> LaunchAsync(string sessionId, string? workingDirectory = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new SessionLaunchResult(99, "pwsh.exe", "copilot --resume " + sessionId, workingDirectory ?? ""));
        public Task<SessionLaunchResult> LaunchNewAsync(string? workingDirectory = null, CancellationToken cancellationToken = default)
        {
            NewCalls++;
            LastNewCwd = workingDirectory;
            return Task.FromResult(new SessionLaunchResult(4242, "pwsh.exe", "copilot", workingDirectory ?? ""));
        }
    }

    private sealed class ThrowingLauncher : ISessionLauncher
    {
        private readonly string _msg;
        public ThrowingLauncher(string msg) => _msg = msg;
        public Task<SessionLaunchResult> LaunchAsync(string sessionId, string? workingDirectory = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(_msg);
        public Task<SessionLaunchResult> LaunchNewAsync(string? workingDirectory = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(_msg);
    }
}

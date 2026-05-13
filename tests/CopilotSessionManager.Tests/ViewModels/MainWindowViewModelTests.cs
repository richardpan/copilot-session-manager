using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Cli;
using CopilotSessionManager.Core.GitHub;
using CopilotSessionManager.Core.Logging;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using CopilotSessionManager.Core.Settings;
using CopilotSessionManager.Logging;
using CopilotSessionManager.Services;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels;

public class MainWindowViewModelTests
{
    private static MainWindowViewModel CreateSut(
        FakeAppSettingsStore? settingsStore = null,
        FakeLogBundler? logBundler = null,
        LogLevelSwitchAccessor? levelSwitch = null,
        FakeFileLauncher? fileLauncher = null,
        IGitHubAvailabilityProvider? availability = null,
        IUiDispatcher? dispatcher = null,
        ICliAvailabilityProvider? cliAvailability = null,
        ICliVersionProbe? cliVersionProbe = null)
    {
        var sessions = new SessionsViewModel(
            new FakeDiscoveryService(),
            new FakeLabelStore(),
            new FakeReadmeService(),
            new FakeFileLauncher(),
            new SyncDispatcher(),
            TimeProvider.System,
            NullLogger<SessionsViewModel>.Instance);
        return new MainWindowViewModel(
            sessions,
            new ServiceCollection().BuildServiceProvider(),
            settingsStore ?? new FakeAppSettingsStore(),
            logBundler ?? new FakeLogBundler(),
            levelSwitch ?? new LogLevelSwitchAccessor(new LoggingLevelSwitch(LogEventLevel.Information)),
            fileLauncher ?? new FakeFileLauncher(),
            availability,
            dispatcher,
            cliAvailability,
            cliVersionProbe,
            NullLogger<MainWindowViewModel>.Instance);
    }

    [Fact]
    public void Title_DefaultsToProductAndVersion()
    {
        var sut = CreateSut();
        sut.Title.Should().Contain("Copilot Session Manager");
    }

    [Fact]
    public void HeaderText_DefaultsToProductName()
    {
        var sut = CreateSut();
        sut.HeaderText.Should().Be("Copilot Session Manager");
    }

    [Fact]
    public void StatusBarText_HasDefault()
    {
        var sut = CreateSut();
        sut.StatusBarText.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Sessions_IsNotNull()
    {
        var sut = CreateSut();
        sut.Sessions.Should().NotBeNull();
    }

    [Fact]
    public void OutdatedCliBanner_IsConstructed()
    {
        var sut = CreateSut();

        sut.OutdatedCliBanner.Should().NotBeNull();
        sut.OutdatedCliBanner.IsVisible.Should().BeFalse();
    }

    [Fact]
    public void OutdatedCliBanner_UpdatesWhenProviderReportsOutdatedState()
    {
        var cliAvailability = new CliAvailabilityProvider();
        var sut = CreateSut(cliAvailability: cliAvailability);

        cliAvailability.Report(
            CliAvailability.Outdated,
            new[] { new CliVersionInfo("gh", new Version(2, 39, 0), new Version(2, 40, 0), true, "gh version 2.39.0") },
            "old gh");

        sut.OutdatedCliBanner.IsVisible.Should().BeTrue();
        sut.OutdatedCliBanner.Headline.Should().Contain("GitHub CLI 2.39.0");
    }

    [Fact]
    public void Property_RaisesPropertyChanged_WhenSet()
    {
        var sut = CreateSut();
        var raised = false;
        sut.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.HeaderText))
            {
                raised = true;
            }
        };

        sut.HeaderText = "changed";

        raised.Should().BeTrue();
    }

    [Fact]
    public void IsVerboseLogging_DefaultsFromSwitch_AndIsFalseAtInformation()
    {
        var sut = CreateSut();
        sut.IsVerboseLogging.Should().BeFalse();
    }

    [Fact]
    public async Task ToggleVerboseLogging_FlipsLevel_AndPersists()
    {
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
        var accessor = new LogLevelSwitchAccessor(levelSwitch);
        var settings = new FakeAppSettingsStore();
        var sut = CreateSut(settingsStore: settings, levelSwitch: accessor);

        sut.IsVerboseLogging = true;
        await sut.ToggleVerboseLoggingCommand.ExecuteAsync(null);

        levelSwitch.MinimumLevel.Should().Be(LogEventLevel.Debug);
        settings.LastSaved!.LogLevel.Should().Be("Debug");

        sut.IsVerboseLogging = false;
        await sut.ToggleVerboseLoggingCommand.ExecuteAsync(null);

        levelSwitch.MinimumLevel.Should().Be(LogEventLevel.Information);
        settings.LastSaved!.LogLevel.Should().Be("Information");
    }

    [Fact]
    public async Task OpenLogFolder_DelegatesToFileLauncher()
    {
        var launcher = new FakeFileLauncher();
        var sut = CreateSut(fileLauncher: launcher);

        await sut.OpenLogFolderCommand.ExecuteAsync(null);

        launcher.OpenedPaths.Should().ContainSingle();
    }

    [Fact]
    public async Task RunStartupTasks_LoadsSettings_AndForwardsAutoCleanFlag()
    {
        var settings = new FakeAppSettingsStore
        {
            Current = new AppSettings { AutoCleanStaleLocksOnStartup = true },
        };
        var sut = CreateSut(settingsStore: settings);

        await sut.RunStartupTasksAsync();

        settings.LoadCount.Should().Be(1,
            "RunStartupTasksAsync must consult settings to decide whether to opt into the V1.8 (#74) auto-clean");
    }

    [Fact]
    public async Task RunStartupTasks_DefaultsToNoAutoClean_WhenSettingsLoadThrows()
    {
        var settings = new FakeAppSettingsStore { ThrowOnLoad = true };
        var sut = CreateSut(settingsStore: settings);

        var act = () => sut.RunStartupTasksAsync();
        await act.Should().NotThrowAsync(
            "settings load failures must never block the dashboard from initialising");
    }

    [Fact]
    public async Task RunStartupTasks_CanBeCalledTwice_Idempotent()
    {
        // SessionsViewModel.InitializeAsync short-circuits on the second call;
        // RunStartupTasksAsync must be safe to invoke more than once (e.g.
        // re-entrant Loaded events from XAML reattachment).
        var settings = new FakeAppSettingsStore();
        var sut = CreateSut(settingsStore: settings);

        await sut.RunStartupTasksAsync();
        var act = () => sut.RunStartupTasksAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void NoAvailabilityProvider_DefaultsAreNotOfflineAndNotUnauth()
    {
        var sut = CreateSut(availability: null);

        sut.IsGitHubOffline.Should().BeFalse();
        sut.IsGitHubUnauthenticated.Should().BeFalse();
        sut.GitHubStatusMessage.Should().BeEmpty();
    }

    [Fact]
    public void AvailabilityProviderInitialState_AppliedAtConstruction()
    {
        var availability = new GitHubAvailabilityProvider();
        availability.Report(GitHubAvailability.Offline, "no network");
        var sut = CreateSut(availability: availability);

        sut.IsGitHubOffline.Should().BeTrue();
        sut.IsGitHubUnauthenticated.Should().BeFalse();
        sut.GitHubStatusMessage.Should().Be("no network");
    }

    [Fact]
    public void OfflineTransition_FlipsIsGitHubOffline_AndPopulatesMessage()
    {
        var availability = new GitHubAvailabilityProvider();
        var sut = CreateSut(availability: availability);

        availability.Report(GitHubAvailability.Offline, "GitHub appears to be offline.");

        sut.IsGitHubOffline.Should().BeTrue();
        sut.IsGitHubUnauthenticated.Should().BeFalse();
        sut.GitHubStatusMessage.Should().Contain("offline");
    }

    [Fact]
    public void UnauthenticatedTransition_FlipsIsGitHubUnauthenticated_AndPopulatesMessage()
    {
        var availability = new GitHubAvailabilityProvider();
        var sut = CreateSut(availability: availability);

        availability.Report(GitHubAvailability.Unauthenticated, "Run gh auth login.");

        sut.IsGitHubUnauthenticated.Should().BeTrue();
        sut.IsGitHubOffline.Should().BeFalse();
        sut.GitHubStatusMessage.Should().Contain("gh auth login");
    }

    [Fact]
    public void RecoveryTransition_ClearsBothFlagsAndMessage()
    {
        var availability = new GitHubAvailabilityProvider();
        var sut = CreateSut(availability: availability);

        availability.Report(GitHubAvailability.Offline, "no network");
        sut.IsGitHubOffline.Should().BeTrue();

        availability.Report(GitHubAvailability.Available);

        sut.IsGitHubOffline.Should().BeFalse();
        sut.IsGitHubUnauthenticated.Should().BeFalse();
        sut.GitHubStatusMessage.Should().BeEmpty();
    }

    [Fact]
    public void DispatcherIsUsed_ForCrossThreadAvailabilityUpdates()
    {
        var availability = new GitHubAvailabilityProvider();
        var dispatcher = new RecordingDispatcher();
        var sut = CreateSut(availability: availability, dispatcher: dispatcher);

        availability.Report(GitHubAvailability.Offline, "msg");

        dispatcher.PostCount.Should().BeGreaterThan(0);
        sut.IsGitHubOffline.Should().BeTrue();
    }

    [Fact]
    public void GitHubStatusMessage_RaisesPropertyChanged()
    {
        var availability = new GitHubAvailabilityProvider();
        var sut = CreateSut(availability: availability);
        var raised = false;
        sut.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.GitHubStatusMessage))
            {
                raised = true;
            }
        };

        availability.Report(GitHubAvailability.Offline, "msg");

        raised.Should().BeTrue();
    }

    private sealed class RecordingDispatcher : IUiDispatcher
    {
        public int PostCount { get; private set; }
        public void Post(Action action)
        {
            PostCount++;
            action();
        }
    }

    private sealed class FakeDiscoveryService : ISessionDiscoveryService
    {
        public IReadOnlyList<Session> CurrentSessions { get; } = Array.Empty<Session>();
#pragma warning disable CS0067 // Event never invoked: needed only to satisfy the interface.
        public event EventHandler<SessionsChangedEventArgs>? SessionsChanged;
#pragma warning restore CS0067
        public Task<IReadOnlyList<Session>> ScanAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CurrentSessions);
        public Task StartWatchingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopWatchingAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeLabelStore : ISessionLabelStore
    {
#pragma warning disable CS0067
        public event EventHandler<SessionLabelChangedEventArgs>? LabelChanged;
#pragma warning restore CS0067
        public Task<SessionType> GetAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(SessionType.Exploratory);
        public Task<IReadOnlyDictionary<string, SessionType>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, SessionType>>(
                new Dictionary<string, SessionType>(StringComparer.OrdinalIgnoreCase));
        public Task SetAsync(string sessionId, SessionType type, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task RemoveAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeReadmeService : ISessionReadmeService
    {
        public string GetReadmePath(string sessionId) => $"/sessions/{sessionId}/SESSION-README.md";
        public Task<string> EnsureAsync(Session session, SessionType label, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);
        public Task AppendAsync(string sessionId, string markdown, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeFileLauncher : IFileLauncher
    {
        public List<string> OpenedPaths { get; } = new();
        public Task OpenAsync(string path, CancellationToken cancellationToken = default)
        {
            OpenedPaths.Add(path);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAppSettingsStore : IAppSettingsStore
    {
        public AppSettings Current { get; set; } = new();
        public AppSettings? LastSaved { get; private set; }
        public int LoadCount { get; private set; }
        public bool ThrowOnLoad { get; set; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            if (ThrowOnLoad)
            {
                throw new System.IO.IOException("simulated load failure");
            }
            return Task.FromResult(Current);
        }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            LastSaved = settings;
            return Task.CompletedTask;
        }

        public Task ResetAsync(CancellationToken cancellationToken = default)
        {
            Current = AppSettings.Defaults();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLogBundler : ILogBundler
    {
        public Task<LogBundleResult> BundleAsync(string destinationPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LogBundleResult(destinationPath, FileCount: 0, TotalBytes: 0));
    }

    private sealed class SyncDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }
}

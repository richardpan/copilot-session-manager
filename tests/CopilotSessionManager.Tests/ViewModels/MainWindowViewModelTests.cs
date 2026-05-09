using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
        FakeFileLauncher? fileLauncher = null)
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

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

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

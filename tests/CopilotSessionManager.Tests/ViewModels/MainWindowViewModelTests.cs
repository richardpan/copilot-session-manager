using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels;

public class MainWindowViewModelTests
{
    private static MainWindowViewModel CreateSut()
    {
        var sessions = new SessionsViewModel(
            new FakeDiscoveryService(),
            new SyncDispatcher(),
            TimeProvider.System,
            NullLogger<SessionsViewModel>.Instance);
        return new MainWindowViewModel(sessions, NullLogger<MainWindowViewModel>.Instance);
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

    private sealed class SyncDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }
}

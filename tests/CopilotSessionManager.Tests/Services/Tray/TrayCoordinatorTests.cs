using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Services.Tray;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Tests.Services.Tray;

/// <summary>
/// Unit tests for <see cref="TrayCoordinator"/> — drives a fake tray icon
/// and verifies that awaiting-input bookkeeping and event forwarding both
/// behave correctly across collection mutations and per-card status flips.
/// </summary>
public class TrayCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);

    private static SessionCardViewModel BuildCard(SessionStatus status, string id = "abcdef1234567890")
    {
        var session = new Session(
            Id: id,
            Cwd: @"C:\ws\repo",
            Repository: "owner/repo",
            Branch: "main",
            Summary: "Test session",
            HostType: "cli",
            CreatedAt: Now.AddMinutes(-30),
            UpdatedAt: Now.AddMinutes(-2),
            TurnCount: 1,
            Status: status,
            CopilotVersion: CopilotVersion.Zero,
            Locks: Array.Empty<SessionLockInfo>());
        return new SessionCardViewModel(session);
    }

    [Fact]
    public void Construction_pushes_initial_count_to_tray()
    {
        var sessions = new ObservableCollection<SessionCardViewModel>
        {
            BuildCard(SessionStatus.AwaitingInput, "1111111111111111"),
            BuildCard(SessionStatus.Working, "2222222222222222"),
            BuildCard(SessionStatus.AwaitingInput, "3333333333333333"),
        };
        var fake = new FakeTrayIconService();

        using var sut = new TrayCoordinator(fake, sessions, onActivate: () => { }, onQuit: () => { });

        sut.AwaitingInputCount.Should().Be(2);
        fake.LastAwaitingInputCount.Should().Be(2);
    }

    [Fact]
    public void Adding_an_awaiting_session_updates_count()
    {
        var sessions = new ObservableCollection<SessionCardViewModel>();
        var fake = new FakeTrayIconService();
        using var sut = new TrayCoordinator(fake, sessions, () => { }, () => { });

        sessions.Add(BuildCard(SessionStatus.AwaitingInput, "aaaaaaaaaaaaaaaa"));

        sut.AwaitingInputCount.Should().Be(1);
        fake.LastAwaitingInputCount.Should().Be(1);
    }

    [Fact]
    public void Removing_an_awaiting_session_updates_count()
    {
        var card = BuildCard(SessionStatus.AwaitingInput, "bbbbbbbbbbbbbbbb");
        var sessions = new ObservableCollection<SessionCardViewModel> { card };
        var fake = new FakeTrayIconService();
        using var sut = new TrayCoordinator(fake, sessions, () => { }, () => { });
        sut.AwaitingInputCount.Should().Be(1);

        sessions.Remove(card);

        sut.AwaitingInputCount.Should().Be(0);
        fake.LastAwaitingInputCount.Should().Be(0);
    }

    [Fact]
    public void Status_change_on_existing_card_updates_count()
    {
        var card = BuildCard(SessionStatus.Working, "cccccccccccccccc");
        var sessions = new ObservableCollection<SessionCardViewModel> { card };
        var fake = new FakeTrayIconService();
        using var sut = new TrayCoordinator(fake, sessions, () => { }, () => { });
        sut.AwaitingInputCount.Should().Be(0);

        var updated = card.Model with { Status = SessionStatus.AwaitingInput };
        card.UpdateFrom(updated);

        sut.AwaitingInputCount.Should().Be(1);
        fake.LastAwaitingInputCount.Should().Be(1);
    }

    [Fact]
    public void Clearing_collection_resets_count()
    {
        var sessions = new ObservableCollection<SessionCardViewModel>
        {
            BuildCard(SessionStatus.AwaitingInput, "1111111111111111"),
            BuildCard(SessionStatus.AwaitingInput, "2222222222222222"),
        };
        var fake = new FakeTrayIconService();
        using var sut = new TrayCoordinator(fake, sessions, () => { }, () => { });
        sut.AwaitingInputCount.Should().Be(2);

        sessions.Clear();

        sut.AwaitingInputCount.Should().Be(0);
        fake.LastAwaitingInputCount.Should().Be(0);
    }

    [Fact]
    public void ActivateRequested_invokes_activate_callback()
    {
        var sessions = new ObservableCollection<SessionCardViewModel>();
        var fake = new FakeTrayIconService();
        var activateCount = 0;
        using var sut = new TrayCoordinator(fake, sessions, () => activateCount++, () => { });

        fake.RaiseActivateRequested();

        activateCount.Should().Be(1);
    }

    [Fact]
    public void OpenRequested_also_invokes_activate_callback()
    {
        var sessions = new ObservableCollection<SessionCardViewModel>();
        var fake = new FakeTrayIconService();
        var activateCount = 0;
        using var sut = new TrayCoordinator(fake, sessions, () => activateCount++, () => { });

        fake.RaiseOpenRequested();

        activateCount.Should().Be(1);
    }

    [Fact]
    public void QuitRequested_invokes_quit_callback()
    {
        var sessions = new ObservableCollection<SessionCardViewModel>();
        var fake = new FakeTrayIconService();
        var quitCount = 0;
        using var sut = new TrayCoordinator(fake, sessions, () => { }, () => quitCount++);

        fake.RaiseQuitRequested();

        quitCount.Should().Be(1);
    }

    [Fact]
    public void Dispose_unsubscribes_from_card_and_tray_events()
    {
        var card = BuildCard(SessionStatus.Working, "dddddddddddddddd");
        var sessions = new ObservableCollection<SessionCardViewModel> { card };
        var fake = new FakeTrayIconService();
        var activateCount = 0;
        var sut = new TrayCoordinator(fake, sessions, () => activateCount++, () => { });
        sut.Dispose();

        fake.RaiseActivateRequested();
        var updated = card.Model with { Status = SessionStatus.AwaitingInput };
        card.UpdateFrom(updated);
        sessions.Add(BuildCard(SessionStatus.AwaitingInput, "eeeeeeeeeeeeeeee"));

        activateCount.Should().Be(0);
        fake.LastAwaitingInputCount.Should().Be(0);
    }

    [Fact]
    public void Constructor_throws_on_null_arguments()
    {
        var sessions = new ObservableCollection<SessionCardViewModel>();
        var fake = new FakeTrayIconService();

        Action a = () => new TrayCoordinator(null!, sessions, () => { }, () => { });
        Action b = () => new TrayCoordinator(fake, null!, () => { }, () => { });
        Action c = () => new TrayCoordinator(fake, sessions, null!, () => { });
        Action d = () => new TrayCoordinator(fake, sessions, () => { }, null!);

        a.Should().Throw<ArgumentNullException>();
        b.Should().Throw<ArgumentNullException>();
        c.Should().Throw<ArgumentNullException>();
        d.Should().Throw<ArgumentNullException>();
    }

    private sealed class FakeTrayIconService : ITrayIconService
    {
        public int? LastAwaitingInputCount { get; private set; }
        public bool IsVisible { get; private set; }
        public bool IsDisposed { get; private set; }

        public event EventHandler? ActivateRequested;
        public event EventHandler? OpenRequested;
        public event EventHandler? QuitRequested;

        public void Show() => IsVisible = true;
        public void Hide() => IsVisible = false;
        public void UpdateAwaitingInputCount(int count) => LastAwaitingInputCount = count;

        public void RaiseActivateRequested() => ActivateRequested?.Invoke(this, EventArgs.Empty);
        public void RaiseOpenRequested() => OpenRequested?.Invoke(this, EventArgs.Empty);
        public void RaiseQuitRequested() => QuitRequested?.Invoke(this, EventArgs.Empty);

        public void Dispose() => IsDisposed = true;
    }
}

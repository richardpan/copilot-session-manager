using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Windows.Media;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Services;
using CopilotSessionManager.Terminal.Hosting;
using CopilotSessionManager.Tests.ViewModels;
using CopilotSessionManager.ViewModels;
using CopilotSessionManager.ViewModels.Terminal;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Tests.Services;

/// <summary>
/// Phase 6D (#159) tests for
/// <see cref="DashboardTerminalTabsCoordinator"/>. Exercises both
/// directions of selection sync, the no-feedback-loop guarantee, the
/// no-paired-tab quiet path, and disposal cleanup.
/// </summary>
public sealed class DashboardTerminalTabsCoordinatorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 5, 18, 12, 0, 0, TimeSpan.Zero);
    private readonly RecordingFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void Selecting_a_card_with_an_open_tab_activates_that_tab()
    {
        var sessions = CreateSessions(out var alphaCard, out var betaCard);
        var tabs = new TerminalTabsViewModel(_factory);
        var alphaTab = tabs.OpenOrActivate(alphaCard.Model, "Alpha", Brushes.Red);
        var betaTab = tabs.OpenOrActivate(betaCard.Model, "Beta", Brushes.Blue);
        tabs.ActiveTab.Should().BeSameAs(betaTab);

        using var coordinator = new DashboardTerminalTabsCoordinator(sessions, tabs);

        sessions.SelectedCard = alphaCard;

        tabs.ActiveTab.Should().BeSameAs(alphaTab);
    }

    [Fact]
    public void Selecting_a_card_without_an_open_tab_leaves_the_active_tab_alone()
    {
        var sessions = CreateSessions(out var alphaCard, out var betaCard);
        var tabs = new TerminalTabsViewModel(_factory);
        var alphaTab = tabs.OpenOrActivate(alphaCard.Model, "Alpha", Brushes.Red);

        using var coordinator = new DashboardTerminalTabsCoordinator(sessions, tabs);

        sessions.SelectedCard = betaCard;

        tabs.ActiveTab.Should().BeSameAs(alphaTab, because: "no tab is open for beta so the active tab does not change");
    }

    [Fact]
    public void Activating_a_tab_selects_the_matching_card()
    {
        var sessions = CreateSessions(out var alphaCard, out var betaCard);
        var tabs = new TerminalTabsViewModel(_factory);
        var alphaTab = tabs.OpenOrActivate(alphaCard.Model, "Alpha", Brushes.Red);
        var betaTab = tabs.OpenOrActivate(betaCard.Model, "Beta", Brushes.Blue);
        tabs.ActiveTab = alphaTab;
        sessions.SelectedCard = alphaCard;

        using var coordinator = new DashboardTerminalTabsCoordinator(sessions, tabs);

        tabs.ActiveTab = betaTab;

        sessions.SelectedCard.Should().BeSameAs(betaCard);
    }

    [Fact]
    public void Activating_a_tab_whose_card_has_been_removed_leaves_dashboard_selection_alone()
    {
        var sessions = CreateSessions(out var alphaCard, out var betaCard);
        var tabs = new TerminalTabsViewModel(_factory);
        var alphaTab = tabs.OpenOrActivate(alphaCard.Model, "Alpha", Brushes.Red);

        // Synthesize a tab whose SessionId no longer exists in the dashboard.
        var ghostSession = new Session(
            Id: "ghost",
            Cwd: @"C:\\ws\\fake",
            Repository: null,
            Branch: null,
            Summary: null,
            HostType: null,
            CreatedAt: Now,
            UpdatedAt: Now,
            TurnCount: 0,
            Status: SessionStatus.Idle,
            CopilotVersion: CopilotVersion.Zero,
            Locks: Array.Empty<SessionLockInfo>());
        var ghostTab = tabs.OpenOrActivate(ghostSession, "Ghost", Brushes.Gray);
        sessions.SelectedCard = alphaCard;

        using var coordinator = new DashboardTerminalTabsCoordinator(sessions, tabs);

        tabs.ActiveTab = ghostTab;

        sessions.SelectedCard.Should().BeSameAs(alphaCard, because: "the activated tab has no paired card");
    }

    [Fact]
    public void Synced_updates_do_not_form_a_feedback_loop()
    {
        var sessions = CreateSessions(out var alphaCard, out var betaCard);
        var tabs = new TerminalTabsViewModel(_factory);
        var alphaTab = tabs.OpenOrActivate(alphaCard.Model, "Alpha", Brushes.Red);
        var betaTab = tabs.OpenOrActivate(betaCard.Model, "Beta", Brushes.Blue);

        using var coordinator = new DashboardTerminalTabsCoordinator(sessions, tabs);

        var cardChangeCount = 0;
        sessions.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionsViewModel.SelectedCard))
            {
                cardChangeCount++;
            }
        };
        var tabChangeCount = 0;
        tabs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TerminalTabsViewModel.ActiveTab))
            {
                tabChangeCount++;
            }
        };

        sessions.SelectedCard = alphaCard;

        cardChangeCount.Should().Be(1);
        tabChangeCount.Should().Be(1);
    }

    [Fact]
    public void Dispose_unhooks_subscriptions_so_later_changes_do_not_propagate()
    {
        var sessions = CreateSessions(out var alphaCard, out var betaCard);
        var tabs = new TerminalTabsViewModel(_factory);
        var alphaTab = tabs.OpenOrActivate(alphaCard.Model, "Alpha", Brushes.Red);
        var betaTab = tabs.OpenOrActivate(betaCard.Model, "Beta", Brushes.Blue);
        var coordinator = new DashboardTerminalTabsCoordinator(sessions, tabs);

        coordinator.Dispose();

        sessions.SelectedCard = alphaCard;
        tabs.ActiveTab.Should().BeSameAs(betaTab, because: "the coordinator stopped listening on Dispose");
    }

    [Fact]
    public void Constructor_throws_when_arguments_are_null()
    {
        var tabs = new TerminalTabsViewModel(_factory);
        Action a = () => _ = new DashboardTerminalTabsCoordinator(null!, tabs);
        a.Should().Throw<ArgumentNullException>().WithParameterName("sessions");

        var sessions = CreateSessions(out _, out _);
        Action b = () => _ = new DashboardTerminalTabsCoordinator(sessions, null!);
        b.Should().Throw<ArgumentNullException>().WithParameterName("tabs");
    }

    // ---- helpers ----

    private static SessionsViewModel CreateSessions(
        out SessionCardViewModel alpha,
        out SessionCardViewModel beta)
    {
        var alphaSession = NewSession("alpha");
        var betaSession = NewSession("beta");
        var disc = new SessionsViewModelTests.FakeDiscoveryService(new[] { alphaSession, betaSession });
        var vm = new SessionsViewModel(
            disc,
            new SessionsViewModelTests.FakeLabelStore(),
            new SessionsViewModelTests.FakeReadmeService(),
            new SessionsViewModelTests.FakeFileLauncher(),
            new SessionsViewModelTests.SyncDispatcher(),
            new SessionsViewModelTests.FixedTimeProvider(Now),
            NullLogger<SessionsViewModel>.Instance);
        vm.InitializeAsync().GetAwaiter().GetResult();
        alpha = vm.Sessions.Single(c => c.Id == "alpha");
        beta = vm.Sessions.Single(c => c.Id == "beta");
        return vm;
    }

    private static Session NewSession(string id) => new(
        Id: id,
        Cwd: @"C:\\ws\\fake",
        Repository: "owner/repo",
        Branch: "main",
        Summary: id,
        HostType: "cli",
        CreatedAt: Now,
        UpdatedAt: Now,
        TurnCount: 0,
        Status: SessionStatus.Idle,
        CopilotVersion: CopilotVersion.Zero,
        Locks: Array.Empty<SessionLockInfo>());

    private sealed class RecordingFactory : ITerminalSessionFactory, IDisposable
    {
        private readonly List<(FakeTerminalProcess Process, TerminalSession Session)> _created = new();

        public TerminalSession Create(Session session, int rows, int cols)
        {
            var process = new FakeTerminalProcess();
            var ts = new TerminalSession(process, new InlineDispatcher(), rows, cols);
            _created.Add((process, ts));
            return ts;
        }

        public TerminalSession CreateNewCopilotSession(int rows, int cols)
        {
            var process = new FakeTerminalProcess();
            var ts = new TerminalSession(process, new InlineDispatcher(), rows, cols);
            _created.Add((process, ts));
            return ts;
        }

        public void Dispose()
        {
            foreach (var (proc, ts) in _created)
            {
                try
                { ts.Dispose(); }
                catch { }
                try
                { proc.Dispose(); }
                catch { }
            }
            _created.Clear();
        }
    }

    private sealed class InlineDispatcher : ITerminalDispatcher
    {
        public void Post(Action action) => action();
    }

    private sealed class FakeTerminalProcess : ITerminalProcess
    {
        private readonly AnonymousPipeServerStream _stdoutWriteSide;
        private readonly AnonymousPipeClientStream _stdoutReadSide;
        private readonly AnonymousPipeServerStream _stdinWriteSide;
        private readonly AnonymousPipeClientStream _stdinReadSide;
        private bool _hasExited;
        private bool _disposed;

        public FakeTerminalProcess()
        {
            _stdoutWriteSide = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            _stdoutReadSide = new AnonymousPipeClientStream(PipeDirection.In, _stdoutWriteSide.ClientSafePipeHandle);
            _stdinWriteSide = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            _stdinReadSide = new AnonymousPipeClientStream(PipeDirection.In, _stdinWriteSide.ClientSafePipeHandle);
        }

        public Stream InputStream => _stdinWriteSide;
        public Stream OutputStream => _stdoutReadSide;
        public bool HasExited => _hasExited;
        public void Resize(short cols, short rows) { }

        public void Dispose()
        {
            if (_disposed)
            { return; }
            _disposed = true;
            _hasExited = true;
            try
            { _stdoutWriteSide.Dispose(); }
            catch { }
            try
            { _stdoutReadSide.Dispose(); }
            catch { }
            try
            { _stdinWriteSide.Dispose(); }
            catch { }
            try
            { _stdinReadSide.Dispose(); }
            catch { }
        }
    }
}

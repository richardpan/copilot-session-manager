using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Terminal.Hosting;
using CopilotSessionManager.ViewModels.Terminal;
using FluentAssertions;

namespace CopilotSessionManager.Tests.ViewModels.Terminal;

/// <summary>
/// Phase 6A scaffolding tests for <see cref="TerminalTabsViewModel"/>.
/// Covers find-or-create activation, close + dispose semantics, and the
/// <c>IsActive</c> projection onto the per-tab view-model.
/// </summary>
public sealed class TerminalTabsViewModelTests : IDisposable
{
    private readonly RecordingFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void OpenOrActivate_creates_new_tab_when_id_not_present()
    {
        var sut = new TerminalTabsViewModel(_factory);
        var session = NewSession("alpha");

        var tab = sut.OpenOrActivate(session, "Alpha", Brushes.Red);

        sut.Tabs.Should().ContainSingle().Which.Should().BeSameAs(tab);
        sut.ActiveTab.Should().BeSameAs(tab);
        tab.SessionId.Should().Be("alpha");
        tab.DisplayName.Should().Be("Alpha");
        tab.TierAccent.Should().BeSameAs(Brushes.Red);
        tab.IsActive.Should().BeTrue();
        sut.IsEmpty.Should().BeFalse();
        _factory.CreateCallCount.Should().Be(1);
    }

    [Fact]
    public void OpenOrActivate_activates_existing_tab_when_id_present()
    {
        var sut = new TerminalTabsViewModel(_factory);
        var alpha = sut.OpenOrActivate(NewSession("alpha"), "Alpha", Brushes.Red);
        var beta = sut.OpenOrActivate(NewSession("beta"), "Beta", Brushes.Blue);
        sut.ActiveTab.Should().BeSameAs(beta);

        var reopened = sut.OpenOrActivate(NewSession("alpha"), "Alpha (renamed)", Brushes.Green);

        reopened.Should().BeSameAs(alpha);
        sut.Tabs.Should().HaveCount(2);
        sut.ActiveTab.Should().BeSameAs(alpha);
        alpha.IsActive.Should().BeTrue();
        beta.IsActive.Should().BeFalse();
        alpha.DisplayName.Should().Be("Alpha (renamed)");
        alpha.TierAccent.Should().BeSameAs(Brushes.Green);
        _factory.CreateCallCount.Should().Be(2);
    }

    [Fact]
    public void OpenOrActivate_raises_ActiveTab_property_change()
    {
        var sut = new TerminalTabsViewModel(_factory);
        var raised = 0;
        sut.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TerminalTabsViewModel.ActiveTab))
            {
                raised++;
            }
        };

        sut.OpenOrActivate(NewSession("alpha"), "Alpha", Brushes.Red);
        sut.OpenOrActivate(NewSession("beta"), "Beta", Brushes.Blue);

        raised.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Close_removes_tab_and_disposes_its_session()
    {
        var sut = new TerminalTabsViewModel(_factory);
        var tab = sut.OpenOrActivate(NewSession("alpha"), "Alpha", Brushes.Red);

        sut.Close(tab);

        sut.Tabs.Should().BeEmpty();
        sut.ActiveTab.Should().BeNull();
        sut.IsEmpty.Should().BeTrue();
        tab.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void Close_on_active_tab_activates_the_neighbour_at_same_index()
    {
        var sut = new TerminalTabsViewModel(_factory);
        var alpha = sut.OpenOrActivate(NewSession("alpha"), "Alpha", Brushes.Red);
        var beta = sut.OpenOrActivate(NewSession("beta"), "Beta", Brushes.Blue);
        var gamma = sut.OpenOrActivate(NewSession("gamma"), "Gamma", Brushes.Green);
        sut.ActiveTab = beta;

        sut.Close(beta);

        sut.Tabs.Should().HaveCount(2);
        // Beta sat at index 1; after removal index 1 is now gamma.
        sut.ActiveTab.Should().BeSameAs(gamma);
        alpha.IsActive.Should().BeFalse();
        gamma.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Close_on_rightmost_active_tab_activates_the_new_rightmost()
    {
        var sut = new TerminalTabsViewModel(_factory);
        var alpha = sut.OpenOrActivate(NewSession("alpha"), "Alpha", Brushes.Red);
        var beta = sut.OpenOrActivate(NewSession("beta"), "Beta", Brushes.Blue);
        sut.ActiveTab = beta;

        sut.Close(beta);

        sut.ActiveTab.Should().BeSameAs(alpha);
        alpha.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Close_on_inactive_tab_leaves_active_tab_unchanged()
    {
        var sut = new TerminalTabsViewModel(_factory);
        var alpha = sut.OpenOrActivate(NewSession("alpha"), "Alpha", Brushes.Red);
        var beta = sut.OpenOrActivate(NewSession("beta"), "Beta", Brushes.Blue);
        sut.ActiveTab = beta;

        sut.Close(alpha);

        sut.Tabs.Should().ContainSingle().Which.Should().BeSameAs(beta);
        sut.ActiveTab.Should().BeSameAs(beta);
        beta.IsActive.Should().BeTrue();
        alpha.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void Close_on_tab_not_in_strip_is_a_noop()
    {
        var sut = new TerminalTabsViewModel(_factory);
        var alpha = sut.OpenOrActivate(NewSession("alpha"), "Alpha", Brushes.Red);

        // Create an orphan tab not owned by the view-model and close it.
        var orphanProcess = new FakeTerminalProcess();
        var orphanSession = new TerminalSession(orphanProcess, new InlineDispatcher(), rows: 10, cols: 40);
        try
        {
            var orphan = new TerminalTabViewModel("orphan", "Orphan", Brushes.Gray, orphanSession);
            try
            {
                sut.Close(orphan);

                sut.Tabs.Should().ContainSingle().Which.Should().BeSameAs(alpha);
                sut.ActiveTab.Should().BeSameAs(alpha);
                orphan.IsDisposed.Should().BeFalse();
            }
            finally
            {
                orphan.Dispose();
            }
        }
        finally
        {
            orphanSession.Dispose();
        }
    }

    [Fact]
    public void CloseAll_clears_strip_and_disposes_every_tab()
    {
        var sut = new TerminalTabsViewModel(_factory);
        var alpha = sut.OpenOrActivate(NewSession("alpha"), "Alpha", Brushes.Red);
        var beta = sut.OpenOrActivate(NewSession("beta"), "Beta", Brushes.Blue);
        var gamma = sut.OpenOrActivate(NewSession("gamma"), "Gamma", Brushes.Green);

        sut.CloseAll();

        sut.Tabs.Should().BeEmpty();
        sut.ActiveTab.Should().BeNull();
        sut.IsEmpty.Should().BeTrue();
        alpha.IsDisposed.Should().BeTrue();
        beta.IsDisposed.Should().BeTrue();
        gamma.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void Dispose_calls_CloseAll()
    {
        var sut = new TerminalTabsViewModel(_factory);
        var alpha = sut.OpenOrActivate(NewSession("alpha"), "Alpha", Brushes.Red);

        sut.Dispose();

        sut.Tabs.Should().BeEmpty();
        alpha.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void Setting_ActiveTab_directly_updates_IsActive_on_old_and_new_tabs()
    {
        var sut = new TerminalTabsViewModel(_factory);
        var alpha = sut.OpenOrActivate(NewSession("alpha"), "Alpha", Brushes.Red);
        var beta = sut.OpenOrActivate(NewSession("beta"), "Beta", Brushes.Blue);
        beta.IsActive.Should().BeTrue();
        alpha.IsActive.Should().BeFalse();

        sut.ActiveTab = alpha;

        alpha.IsActive.Should().BeTrue();
        beta.IsActive.Should().BeFalse();

        sut.ActiveTab = null;
        alpha.IsActive.Should().BeFalse();
        beta.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Constructor_throws_when_factory_is_null()
    {
        Action act = () => _ = new TerminalTabsViewModel(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("sessionFactory");
    }

    [Fact]
    public void OpenOrActivate_throws_on_null_arguments()
    {
        var sut = new TerminalTabsViewModel(_factory);
        var session = NewSession("alpha");

        sut.Invoking(s => s.OpenOrActivate(null!, "x", Brushes.Red))
            .Should().Throw<ArgumentNullException>().WithParameterName("session");
        sut.Invoking(s => s.OpenOrActivate(session, null!, Brushes.Red))
            .Should().Throw<ArgumentNullException>().WithParameterName("displayName");
        sut.Invoking(s => s.OpenOrActivate(session, "x", null!))
            .Should().Throw<ArgumentNullException>().WithParameterName("tierAccent");
    }

    [Fact]
    public void TerminalTabViewModel_dispose_is_idempotent()
    {
        var process = new FakeTerminalProcess();
        var session = new TerminalSession(process, new InlineDispatcher(), rows: 10, cols: 40);
        try
        {
            var tab = new TerminalTabViewModel("alpha", "Alpha", Brushes.Red, session);
            tab.Dispose();
            tab.Dispose();
            tab.IsDisposed.Should().BeTrue();
        }
        finally
        {
            session.Dispose();
        }
    }

    private static Session NewSession(string id) => new(
        Id: id,
        Cwd: @"C:\\ws\\fake",
        Repository: null,
        Branch: null,
        Summary: null,
        HostType: null,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow,
        TurnCount: 0,
        Status: SessionStatus.Idle,
        CopilotVersion: CopilotVersion.Zero,
        Locks: Array.Empty<SessionLockInfo>());

    /// <summary>Records each Create call and produces a fresh in-memory TerminalSession.</summary>
    private sealed class RecordingFactory : ITerminalSessionFactory, IDisposable
    {
        private readonly System.Collections.Generic.List<(FakeTerminalProcess Process, TerminalSession Session)> _created = new();

        public int CreateCallCount => _created.Count;

        public TerminalSession Create(Session session, int rows, int cols)
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

    /// <summary>Dispatcher that runs posted actions synchronously on the caller's thread.</summary>
    private sealed class InlineDispatcher : ITerminalDispatcher
    {
        public void Post(Action action) => action();
    }

    /// <summary>
    /// Minimal in-memory <see cref="ITerminalProcess"/> built on anonymous
    /// pipes. Mirrors the helper from
    /// <c>CopilotSessionManager.Terminal.Hosting.Tests</c> (Phase 3E)
    /// but kept private here to avoid an internals-visible-to dependency.
    /// </summary>
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

        public void Resize(short cols, short rows) { /* no-op for fake */ }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
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

using System;
using System.Windows;
using System.Windows.Threading;
using CopilotSessionManager.Terminal;
using CopilotSessionManager.Terminal.Hosting;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Views;

/// <summary>
/// Phase 3E debug surface. Hosts a <c>TerminalControl</c> wired to a
/// fresh <see cref="TerminalSession"/> over <c>pwsh -NoLogo</c> so an
/// engineer can validate the end-to-end ConPTY ↔ parser ↔ buffer ↔
/// renderer pipeline interactively.
/// </summary>
public partial class TerminalWindow : Window
{
    private readonly ILogger<TerminalWindow>? _logger;
    private readonly DispatcherTimer _resizeDebounceTimer;
    private TerminalSession? _session;
    private Size _pendingTerminalSize;
    private int _lastResizeRows;
    private int _lastResizeCols;

    /// <summary>
    /// Initial pseudo-console dimensions. Tuned to fit the default window
    /// size at the chosen font without immediately triggering a resize.
    /// </summary>
    private const int InitialRows = 30;
    private const int InitialCols = 100;

    public TerminalWindow()
        : this(null)
    {
    }

    public TerminalWindow(ILogger<TerminalWindow>? logger)
    {
        InitializeComponent();
        _logger = logger;
        _lastResizeRows = InitialRows;
        _lastResizeCols = InitialCols;
        _resizeDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _resizeDebounceTimer.Tick += OnResizeDebounceTick;
        Terminal.SizeChanged += OnTerminalSizeChanged;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var dispatcher = new WpfTerminalDispatcher(Dispatcher);
            _session = TerminalSession.Start("pwsh.exe -NoLogo", InitialRows, InitialCols, dispatcher);
            _lastResizeRows = InitialRows;
            _lastResizeCols = InitialCols;
            Terminal.Buffer = _session.Buffer;
            Terminal.InputProduced += OnInputProduced;
            _session.Exited += OnSessionExited;
            StatusText.Text = $"pwsh.exe session running ({InitialCols}×{InitialRows}).";
            Terminal.Focus();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to start embedded terminal session.");
            StatusText.Text = $"Failed to start pwsh: {ex.Message}";
        }
    }

    private void OnInputProduced(object? sender, Terminal.Wpf.TerminalInputEventArgs e)
    {
        try
        {
            _session?.SendInput(e.Bytes.Span);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to forward input bytes to embedded terminal session.");
        }
    }

    private void OnTerminalSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _pendingTerminalSize = e.NewSize;
        _resizeDebounceTimer.Stop();
        _resizeDebounceTimer.Start();
    }

    private void OnResizeDebounceTick(object? sender, EventArgs e)
    {
        _resizeDebounceTimer.Stop();
        var session = _session;
        if (session is null)
        {
            return;
        }

        var (rows, cols) = Terminal.CellsForViewport(_pendingTerminalSize);
        rows = Math.Max(2, rows);
        cols = Math.Max(2, cols);
        if (rows == _lastResizeRows && cols == _lastResizeCols)
        {
            return;
        }

        try
        {
            session.Resize(rows, cols);
            _lastResizeRows = rows;
            _lastResizeCols = cols;
            StatusText.Text = $"pwsh.exe session running ({cols}×{rows}).";
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to resize embedded terminal session.");
        }
    }

    private void OnSessionExited(object? sender, EventArgs e)
    {
        StatusText.Text = "Session exited.";
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _resizeDebounceTimer.Stop();
        Terminal.SizeChanged -= OnTerminalSizeChanged;
        if (_session is not null)
        {
            Terminal.InputProduced -= OnInputProduced;
            _session.Exited -= OnSessionExited;
            try
            {
                _session.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Embedded terminal session dispose raised.");
            }
            _session = null;
        }
    }
}

/// <summary>
/// WPF <see cref="Dispatcher"/>-backed implementation of
/// <see cref="ITerminalDispatcher"/>. Lives here (rather than in the
/// Hosting library) so the library can stay WPF-free and unit-testable.
/// </summary>
internal sealed class WpfTerminalDispatcher : ITerminalDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfTerminalDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.BeginInvoke(action);
    }
}

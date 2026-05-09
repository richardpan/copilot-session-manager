using System;
using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace CopilotSessionManager.Services.Tray;

/// <summary>
/// Windows-only <see cref="ITrayIconService"/> backed by
/// <see cref="NotifyIcon"/> from <c>System.Windows.Forms</c>. WPF + WinForms
/// interop is enabled via <c>&lt;UseWindowsForms&gt;true&lt;/UseWindowsForms&gt;</c>
/// in the host csproj.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NotifyIconTrayService : ITrayIconService
{
    private const string ProductName = "Copilot Session Manager";

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _quitItem;
    private bool _disposed;

    public NotifyIconTrayService()
    {
        _menu = new ContextMenuStrip();
        _openItem = new ToolStripMenuItem("Open");
        _openItem.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        _quitItem = new ToolStripMenuItem("Quit");
        _quitItem.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);
        _menu.Items.Add(_openItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_quitItem);

        _notifyIcon = new NotifyIcon
        {
            // SystemIcons.Application is always available so a missing
            // packaged .ico resource never crashes the host on first run.
            // A proper branded icon lands in a follow-up issue.
            Icon = SystemIcons.Application,
            Text = ProductName,
            ContextMenuStrip = _menu,
            Visible = false,
        };

        // Single left-click is the conventional "restore" gesture on Windows.
        _notifyIcon.MouseClick += OnMouseClick;
    }

    public event EventHandler? ActivateRequested;
    public event EventHandler? OpenRequested;
    public event EventHandler? QuitRequested;

    public void Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _notifyIcon.Visible = true;
    }

    public void Hide()
    {
        if (_disposed)
        {
            return;
        }
        _notifyIcon.Visible = false;
    }

    public void UpdateAwaitingInputCount(int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // NotifyIcon.Text has a hard 127-character ceiling (it maps onto
        // NOTIFYICONDATA.szTip); the messages here stay well under that.
        _notifyIcon.Text = count > 0
            ? $"{ProductName} ({count} awaiting input)"
            : ProductName;
    }

    public void ShowNotification(string title, string body, bool isError = false)
    {
        if (_disposed)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        try
        {
            // NotifyIcon.ShowBalloonTip silently no-ops when the icon is
            // hidden, so guard so a stray notification doesn't surface a
            // ghost icon. The 4s timeout matches the OS default — newer
            // Windows builds clamp it but the value is still required.
            if (!_notifyIcon.Visible)
            {
                return;
            }
            var icon = isError ? ToolTipIcon.Warning : ToolTipIcon.Info;
            _notifyIcon.ShowBalloonTip(4_000, title ?? string.Empty, body ?? string.Empty, icon);
        }
        catch
        {
            // Best-effort; never let a UI notification take down the host.
        }
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ActivateRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _notifyIcon.MouseClick -= OnMouseClick;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }
}

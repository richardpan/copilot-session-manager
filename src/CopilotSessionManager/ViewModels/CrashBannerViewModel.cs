using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CopilotSessionManager.ViewModels;

/// <summary>
/// Session-scoped notification banner for crashed/orphaned sessions.
/// Dismissed ids intentionally live only for the current app process.
/// </summary>
public sealed partial class CrashBannerViewModel : ObservableObject
{
    private readonly SessionsViewModel _sessions;
    private readonly HashSet<string> _dismissedIds = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string _message = FormatMessage(0);

    [ObservableProperty]
    private int _crashedCount;

    public CrashBannerViewModel(SessionsViewModel sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        _sessions = sessions;
        DismissCommand = new RelayCommand(Dismiss);
        CleanUpAllCommand = new AsyncRelayCommand(CleanUpAllAsync, CanCleanUpAll);
        Refresh();
    }

    public IRelayCommand DismissCommand { get; }

    public IAsyncRelayCommand CleanUpAllCommand { get; }

    public void Refresh()
    {
        var crashedIds = _sessions.Sessions
            .Where(static session => session.IsCrashed)
            .Select(static session => session.Id)
            .ToArray();

        CrashedCount = crashedIds.Length;
        Message = FormatMessage(crashedIds.Length);
        IsVisible = crashedIds.Except(_dismissedIds, StringComparer.OrdinalIgnoreCase).Any();
        CleanUpAllCommand.NotifyCanExecuteChanged();
    }

    private void Dismiss()
    {
        foreach (var id in _sessions.Sessions.Where(static session => session.IsCrashed).Select(static session => session.Id))
        {
            // Keep ids dismissed for the whole app session, even if cleanup later
            // flips them non-crashed and a rare re-orphan reports the same id again.
            _dismissedIds.Add(id);
        }

        Refresh();
    }

    private bool CanCleanUpAll() => _sessions.CleanAllStaleLocksCommand.CanExecute(null);

    private async Task CleanUpAllAsync()
    {
        if (_sessions.CleanAllStaleLocksCommand.CanExecute(null))
        {
            await _sessions.CleanAllStaleLocksCommand.ExecuteAsync(null).ConfigureAwait(true);
        }

        Refresh();
    }

    private static string FormatMessage(int count) =>
        count == 1
            ? "1 session crashed since last scan."
            : $"{count} sessions crashed since last scan.";
}

using System;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CopilotSessionManager.Core.GitHub.Issues;
using CopilotSessionManager.Services;

namespace CopilotSessionManager.ViewModels;

/// <summary>
/// Display projection over a single linked GitHub issue. Owns the badge
/// brush + tooltip + Open / Remove commands. Open delegates to the shared
/// <see cref="IFileLauncher"/>; Remove delegates back to the parent
/// <see cref="IssueLinksViewModel"/> so the ref drops from storage and the
/// observable collection in lockstep.
/// </summary>
public sealed partial class IssueLinkViewModel : ObservableObject
{
    // Catppuccin Mocha-aligned palette, matched to the existing PR badge.
    private static readonly Brush OpenBrush = Frozen(Color.FromRgb(0xA6, 0xE3, 0xA1));   // green
    private static readonly Brush ClosedBrush = Frozen(Color.FromRgb(0xCB, 0xA6, 0xF7)); // purple
    private static readonly Brush UnknownBrush = Frozen(Color.FromRgb(0x6C, 0x70, 0x86)); // gray

    private readonly IFileLauncher? _fileLauncher;
    private readonly Func<IssueRef, System.Threading.Tasks.Task> _removeCallback;

    private string _title = string.Empty;
    private IssueState _state = IssueState.Unknown;

    public IssueLinkViewModel(
        IssueRef issueRef,
        string sessionOwnerRepo,
        IFileLauncher? fileLauncher,
        Func<IssueRef, System.Threading.Tasks.Task> removeCallback)
    {
        ArgumentNullException.ThrowIfNull(issueRef);
        ArgumentNullException.ThrowIfNull(removeCallback);

        Ref = issueRef;
        SessionOwnerRepo = sessionOwnerRepo ?? string.Empty;
        Url = issueRef.ToCanonicalUrl();
        _fileLauncher = fileLauncher;
        _removeCallback = removeCallback;

        OpenCommand = new AsyncRelayCommand(OpenAsync, CanOpen);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync);
    }

    /// <summary>The canonical issue ref this badge represents.</summary>
    public IssueRef Ref { get; }

    /// <summary>The session's repo (lower-cased) — used to render a short
    /// or qualified <see cref="Display"/> form.</summary>
    public string SessionOwnerRepo { get; }

    /// <summary>Canonical web URL for the issue.</summary>
    public string Url { get; }

    /// <summary>
    /// Compact label rendered inside the badge: <c>#NN</c> when the link
    /// targets the same repo as the session, otherwise <c>owner/repo#NN</c>.
    /// </summary>
    public string Display
    {
        get
        {
            var num = "#" + Ref.Number.ToString(CultureInfo.InvariantCulture);
            return string.Equals(SessionOwnerRepo, Ref.OwnerRepo, StringComparison.OrdinalIgnoreCase)
                ? num
                : $"{Ref.OwnerRepo}{num}";
        }
    }

    /// <summary>The issue's resolved title; empty until metadata arrives.</summary>
    public string Title
    {
        get => _title;
        private set
        {
            if (SetProperty(ref _title, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(Tooltip));
                OnPropertyChanged(nameof(AutomationName));
            }
        }
    }

    /// <summary>The issue's resolved state. Drives <see cref="BadgeBrush"/>.</summary>
    public IssueState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(BadgeBrush));
                OnPropertyChanged(nameof(BadgeGlyph));
                OnPropertyChanged(nameof(Tooltip));
                OnPropertyChanged(nameof(AutomationName));
            }
        }
    }

    /// <summary>Brush for the badge background — colour-coded by state.</summary>
    public Brush BadgeBrush => State switch
    {
        IssueState.Open => OpenBrush,
        IssueState.Closed => ClosedBrush,
        _ => UnknownBrush,
    };

    #region A11y
    /// <summary>
    /// Glyph rendered alongside the issue number so colour-blind users have a
    /// non-colour signal for state. ● = open, ○ = closed, – = unknown. Pair
    /// this with <see cref="Display"/> (e.g. <c>"● #42"</c>) at the view
    /// layer; do not bake it into <see cref="Display"/> because the existing
    /// short-form contract is part of the public API for tests.
    /// </summary>
    public string BadgeGlyph => State switch
    {
        IssueState.Open => "●",
        IssueState.Closed => "○",
        _ => "–",
    };

    /// <summary>
    /// Full screen-reader-friendly description: "Open issue octo/widgets#42 —
    /// Add cool feature". Used for <c>AutomationProperties.Name</c> on the
    /// badge button.
    /// </summary>
    public string AutomationName
    {
        get
        {
            var stateText = State switch
            {
                IssueState.Open => "Open issue",
                IssueState.Closed => "Closed issue",
                _ => "Issue (state unknown)",
            };
            var qualified = $"{Ref.OwnerRepo}#{Ref.Number.ToString(CultureInfo.InvariantCulture)}";
            return string.IsNullOrWhiteSpace(_title)
                ? $"{stateText} {qualified}"
                : $"{stateText} {qualified} — {_title}";
        }
    }
    #endregion

    /// <summary>Tooltip rendered on hover. Falls back to the URL when no title is known yet.</summary>
    public string Tooltip
    {
        get
        {
            var stateText = State switch
            {
                IssueState.Open => "Open",
                IssueState.Closed => "Closed",
                _ => "State unknown",
            };
            var firstLine = $"{Ref.OwnerRepo}#{Ref.Number} — {stateText}";
            return string.IsNullOrWhiteSpace(_title)
                ? $"{firstLine}\n{Url}"
                : $"{firstLine}\n{_title}";
        }
    }

    /// <summary>Opens <see cref="Url"/> in the OS default browser.</summary>
    public ICommand OpenCommand { get; }

    /// <summary>Removes this link from the parent panel and persistent store.</summary>
    public ICommand RemoveCommand { get; }

    /// <summary>
    /// Pushes resolved metadata into the badge. Safe to call multiple times;
    /// raises change notifications for every derived property.
    /// </summary>
    public void ApplyInfo(IssueInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        Title = info.Title;
        State = info.State;
    }

    private bool CanOpen() => _fileLauncher is not null;

    private async System.Threading.Tasks.Task OpenAsync()
    {
        if (_fileLauncher is null)
        {
            return;
        }
        await _fileLauncher.OpenAsync(Url).ConfigureAwait(false);
    }

    private System.Threading.Tasks.Task RemoveAsync() => _removeCallback(Ref);

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

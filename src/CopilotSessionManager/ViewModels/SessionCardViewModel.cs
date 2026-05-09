using System;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CopilotSessionManager.Core.Models;

namespace CopilotSessionManager.ViewModels;

/// <summary>
/// Display projection over a single <see cref="Session"/>. Holds derived
/// strings + brushes so XAML stays declarative.
/// </summary>
public sealed partial class SessionCardViewModel : ObservableObject
{
    private Session _model;
    private readonly TimeProvider _timeProvider;

    public SessionCardViewModel(Session model)
        : this(model, TimeProvider.System)
    {
    }

    public SessionCardViewModel(Session model, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _model = model;
        _timeProvider = timeProvider;
    }

    public Session Model => _model;

    public string Id => _model.Id;

    public string ShortId => _model.Id.Length >= 8 ? _model.Id[..8] : _model.Id;

    public string Title =>
        !string.IsNullOrWhiteSpace(_model.Summary) ? _model.Summary!
        : !string.IsNullOrWhiteSpace(_model.Repository) ? _model.Repository!
        : ShortId;

    public string? Repository => _model.Repository;

    public string? Branch => _model.Branch;

    public string? Cwd => _model.Cwd;

    public string? HostType => _model.HostType;

    public int TurnCount => _model.TurnCount;

    public SessionStatus Status => _model.Status;

    public string StatusLabel => _model.Status switch
    {
        SessionStatus.Working => "Working",
        SessionStatus.AwaitingApproval => "Awaiting approval",
        SessionStatus.AwaitingInput => "Awaiting input",
        SessionStatus.Idle => "Idle",
        SessionStatus.Inactive => "Inactive",
        SessionStatus.Orphaned => "Orphaned",
        _ => "Unknown",
    };

    /// <summary>Color used for the status pill / left edge accent.</summary>
    public Brush StatusBrush => _model.Status switch
    {
        // Working: vibrant green; AwaitingApproval: amber; AwaitingInput: blue;
        // Idle: muted gray; Inactive: dim slate; Orphaned: red.
        SessionStatus.Working => Brushes.MediumSeaGreen,
        SessionStatus.AwaitingApproval => Brushes.Goldenrod,
        SessionStatus.AwaitingInput => Brushes.CornflowerBlue,
        SessionStatus.Idle => Brushes.DarkGray,
        SessionStatus.Inactive => Brushes.SlateGray,
        SessionStatus.Orphaned => Brushes.IndianRed,
        _ => Brushes.Gray,
    };

    public string UpdatedRelative => FormatRelative(_model.UpdatedAt);

    public string Age => FormatDuration(_timeProvider.GetUtcNow() - _model.CreatedAt);

    public string LockSummary => _model.Locks.Count == 0
        ? "no locks"
        : _model.Locks.Count == 1
            ? $"PID {_model.Locks[0].ProcessId}{(_model.Locks[0].IsAlive ? "" : " (dead)")}"
            : $"{_model.Locks.Count} locks";

    /// <summary>
    /// Replaces this card's underlying model and raises change notifications
    /// for every projected property. Used by <see cref="SessionsViewModel"/>
    /// when discovery reports an updated <see cref="Session"/> for the same id.
    /// </summary>
    public void UpdateFrom(Session newModel)
    {
        ArgumentNullException.ThrowIfNull(newModel);
        if (!string.Equals(_model.Id, newModel.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Cannot replace session model with a different id.", nameof(newModel));
        }

        _model = newModel;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Repository));
        OnPropertyChanged(nameof(Branch));
        OnPropertyChanged(nameof(Cwd));
        OnPropertyChanged(nameof(HostType));
        OnPropertyChanged(nameof(TurnCount));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(UpdatedRelative));
        OnPropertyChanged(nameof(Age));
        OnPropertyChanged(nameof(LockSummary));
    }

    private string FormatRelative(DateTimeOffset when)
    {
        if (when == DateTimeOffset.MinValue)
        {
            return "—";
        }

        var delta = _timeProvider.GetUtcNow() - when;
        if (delta < TimeSpan.Zero)
        {
            return "just now";
        }

        return delta.TotalSeconds < 60 ? "just now"
            : delta.TotalMinutes < 60 ? $"{(int)delta.TotalMinutes} min ago"
            : delta.TotalHours < 24 ? $"{(int)delta.TotalHours} hr ago"
            : $"{(int)delta.TotalDays} d ago";
    }

    private static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalSeconds < 60 ? $"{(int)span.TotalSeconds}s"
            : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes}m"
            : span.TotalHours < 24 ? $"{span.Hours}h {span.Minutes}m"
            : $"{(int)span.TotalDays}d {span.Hours}h";
    }
}

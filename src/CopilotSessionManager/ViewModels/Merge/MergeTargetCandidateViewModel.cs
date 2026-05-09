using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.ViewModels;

namespace CopilotSessionManager.ViewModels.Merge;

/// <summary>
/// Lightweight wrapper around a candidate <see cref="SessionCardViewModel"/>
/// that the merge wizard offers as a possible target. Adds an
/// <see cref="IsSelected"/> flag for radio-style binding and a
/// <see cref="RecencyDescription"/> string so the picker list is sortable
/// and human-readable without coupling to the full card.
/// </summary>
public sealed partial class MergeTargetCandidateViewModel : ObservableObject
{
    private readonly TimeProvider _clock;

    [ObservableProperty]
    private bool _isSelected;

    public MergeTargetCandidateViewModel(SessionCardViewModel card, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(clock);
        Card = card;
        _clock = clock;
    }

    /// <summary>The underlying session card. Use for display data.</summary>
    public SessionCardViewModel Card { get; }

    /// <summary>Convenience accessor for the session id.</summary>
    public string Id => Card.Id;

    /// <summary>Convenience accessor for the card's title.</summary>
    public string Title => Card.Title;

    /// <summary>Convenience accessor for the card's short id.</summary>
    public string ShortId => Card.ShortId;

    /// <summary>True when the underlying card is in an active status (working,
    /// awaiting input/approval, or idle but not crashed).</summary>
    public bool IsActive =>
        Card.Status is SessionStatus.Working
            or SessionStatus.AwaitingApproval
            or SessionStatus.AwaitingInput
            or SessionStatus.Idle;

    /// <summary>Status label used by the picker list.</summary>
    public string StatusLabel => Card.StatusLabel;

    /// <summary>Branch / repo string shown as a subtitle in the picker.</summary>
    public string Subtitle
    {
        get
        {
            var repo = Card.Repository;
            var branch = Card.Branch;
            if (!string.IsNullOrWhiteSpace(repo) && !string.IsNullOrWhiteSpace(branch))
            {
                return $"{repo} @ {branch}";
            }
            if (!string.IsNullOrWhiteSpace(repo))
            {
                return repo!;
            }
            if (!string.IsNullOrWhiteSpace(branch))
            {
                return branch!;
            }
            return Card.Cwd ?? string.Empty;
        }
    }

    /// <summary>
    /// Human-readable freshness string for the picker, e.g.
    /// <c>"active 2 min ago"</c>, <c>"updated 3 hr ago"</c>, or
    /// <c>"never updated"</c>. Uses the model's <c>UpdatedAt</c> projection.
    /// </summary>
    public string RecencyDescription
    {
        get
        {
            var when = Card.Model.UpdatedAt;
            if (when == DateTimeOffset.MinValue)
            {
                return "never updated";
            }

            var prefix = IsActive ? "active" : "updated";
            var delta = _clock.GetUtcNow() - when;
            if (delta < TimeSpan.Zero)
            {
                return $"{prefix} just now";
            }

            var ago = delta.TotalSeconds < 60 ? "just now"
                : delta.TotalMinutes < 60 ? $"{(int)delta.TotalMinutes} min ago"
                : delta.TotalHours < 24 ? $"{(int)delta.TotalHours} hr ago"
                : string.Create(CultureInfo.InvariantCulture, $"{(int)delta.TotalDays} d ago");

            return $"{prefix} {ago}";
        }
    }

    /// <summary>
    /// Sort key used by the picker — newer sessions sort first. Returns
    /// <see cref="DateTimeOffset.MinValue"/> when no UpdatedAt is recorded.
    /// </summary>
    public DateTimeOffset SortKey => Card.Model.UpdatedAt;
}

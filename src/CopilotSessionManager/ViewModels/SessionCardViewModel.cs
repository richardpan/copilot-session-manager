using System;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CopilotSessionManager.Core.Cli;
using CopilotSessionManager.Core.Cost;
using CopilotSessionManager.Core.Models;

namespace CopilotSessionManager.ViewModels;

/// <summary>
/// Display projection over a single <see cref="Session"/>. Holds derived
/// strings + brushes so XAML stays declarative.
/// </summary>
public sealed partial class SessionCardViewModel : ObservableObject
{
    private Session _model;
    private SessionType _label;
    private readonly TimeProvider _timeProvider;
    private readonly IModelCatalog? _modelCatalog;
    private readonly IModelCostCalculator? _costCalculator;

    public SessionCardViewModel(Session model)
        : this(model, SessionType.Exploratory, TimeProvider.System, modelCatalog: null, costCalculator: null)
    {
    }

    public SessionCardViewModel(Session model, TimeProvider timeProvider)
        : this(model, SessionType.Exploratory, timeProvider, modelCatalog: null, costCalculator: null)
    {
    }

    public SessionCardViewModel(Session model, SessionType label, TimeProvider timeProvider)
        : this(model, label, timeProvider, modelCatalog: null, costCalculator: null)
    {
    }

    public SessionCardViewModel(
        Session model,
        SessionType label,
        TimeProvider timeProvider,
        IModelCatalog? modelCatalog,
        IModelCostCalculator? costCalculator)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _model = model;
        _label = label;
        _timeProvider = timeProvider;
        _modelCatalog = modelCatalog;
        _costCalculator = costCalculator;
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

    public SessionType Label => _label;

    public string LabelText => _label switch
    {
        SessionType.Exploratory => "Exploratory",
        SessionType.Research => "Research",
        SessionType.Feature => "Feature",
        SessionType.Bug => "Bug",
        SessionType.Refactor => "Refactor",
        SessionType.Docs => "Docs",
        SessionType.Infra => "Infra",
        SessionType.Experiment => "Experiment",
        _ => "Exploratory",
    };

    /// <summary>Color used for the label chip.</summary>
    public Brush LabelBrush => _label switch
    {
        SessionType.Exploratory => Brushes.MediumPurple,
        SessionType.Research => Brushes.MediumSlateBlue,
        SessionType.Feature => Brushes.SteelBlue,
        SessionType.Bug => Brushes.Crimson,
        SessionType.Refactor => Brushes.DarkCyan,
        SessionType.Docs => Brushes.DarkOliveGreen,
        SessionType.Infra => Brushes.SaddleBrown,
        SessionType.Experiment => Brushes.HotPink,
        _ => Brushes.Gray,
    };

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
    /// Resolved model record. <c>null</c> when no model info exists or the
    /// id is not in the embedded catalog.
    /// </summary>
    private CopilotModel? ResolvedModel
    {
        get
        {
            var id = _model.ModelInfo?.CurrentModelId;
            return id is null || _modelCatalog is null ? null : _modelCatalog.Resolve(id);
        }
    }

    /// <summary>
    /// Tier of the resolved model. <see cref="ModelTier.Unknown"/> when the
    /// model is missing or unknown to the catalog.
    /// </summary>
    public ModelTier ModelTier => ResolvedModel?.Tier ?? ModelTier.Unknown;

    /// <summary>Short text shown inside the model badge on the card.</summary>
    public string ModelDisplay
    {
        get
        {
            var resolved = ResolvedModel;
            if (resolved is not null)
            {
                return resolved.DisplayName;
            }
            var rawId = _model.ModelInfo?.CurrentModelId;
            return string.IsNullOrWhiteSpace(rawId) ? "Model unknown" : rawId!;
        }
    }

    /// <summary>Brush used for the model tier chip.</summary>
    public Brush ModelTierBrush => ModelTier switch
    {
        ModelTier.Premium => new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)), // pink
        ModelTier.Standard => new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA)), // blue
        ModelTier.Fast => new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1)), // green
        _ => new SolidColorBrush(Color.FromRgb(0x7F, 0x84, 0x9C)), // gray
    };

    /// <summary>
    /// USD cost estimate. Returns "—" for active sessions (no shutdown event)
    /// or when the model info is missing. Otherwise formats as currency
    /// (US locale) and prefixes "~" when any portion is from an unknown model.
    /// </summary>
    public string CostDisplay
    {
        get
        {
            var result = _costCalculator?.Estimate(_model.ModelInfo);
            if (result is null)
            {
                return "—";
            }
            var formatted = result.UsdAmount.ToString("C2", CultureInfo.GetCultureInfo("en-US"));
            return result.HasUnknownModels ? $"~{formatted}" : formatted;
        }
    }

    /// <summary>Tooltip shown on the model badge.</summary>
    public string ModelTooltip
    {
        get
        {
            var info = _model.ModelInfo;
            if (info is null || string.IsNullOrWhiteSpace(info.CurrentModelId))
            {
                return "Model unknown — no model events recorded for this session yet.";
            }

            var resolved = ResolvedModel;
            var name = resolved?.DisplayName ?? info.CurrentModelId;
            var tier = resolved?.Tier.ToString() ?? "Unknown";
            var sourceLine = info.IsFromShutdown
                ? "Source: session shutdown record (final)."
                : "Source: most recent tool execution (live).";
            var costLine = info.IsFromShutdown
                ? "Cost: estimated, based on default per-million rates."
                : "Cost: only available after the session ends.";
            return $"Model: {name}\nTier: {tier}\n{sourceLine}\n{costLine}";
        }
    }

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
        OnPropertyChanged(nameof(ModelDisplay));
        OnPropertyChanged(nameof(ModelTier));
        OnPropertyChanged(nameof(ModelTierBrush));
        OnPropertyChanged(nameof(CostDisplay));
        OnPropertyChanged(nameof(ModelTooltip));
    }

    /// <summary>
    /// Updates the user-assigned label and raises change notifications for
    /// the label-related projections.
    /// </summary>
    public void UpdateLabel(SessionType label)
    {
        if (_label == label)
        {
            return;
        }

        _label = label;
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(LabelText));
        OnPropertyChanged(nameof(LabelBrush));
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

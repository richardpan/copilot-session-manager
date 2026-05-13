using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CopilotSessionManager.Core.Cli;

namespace CopilotSessionManager.ViewModels;

public sealed partial class OutdatedCliBannerViewModel : ObservableObject
{
    private static readonly Version UnknownCliVersion = new(0, 0, 0);

    private readonly ICliAvailabilityProvider _availability;
    private readonly HashSet<string> _dismissedFingerprints = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string _headline = string.Empty;

    [ObservableProperty]
    private string _upgradeInstructions = string.Empty;

    public OutdatedCliBannerViewModel(ICliAvailabilityProvider availability)
    {
        ArgumentNullException.ThrowIfNull(availability);

        _availability = availability;
        DismissCommand = new RelayCommand(Dismiss);
        _availability.AvailabilityChanged += OnAvailabilityChanged;
        Refresh();
    }

    public IRelayCommand DismissCommand { get; }

    public void Refresh()
    {
        var state = _availability.Current;
        Headline = BuildHeadline(state);
        UpgradeInstructions = BuildUpgradeInstructions(state.Probes);
        IsVisible = state.State != CliAvailability.Available
            && !_dismissedFingerprints.Contains(Fingerprint(state));
    }

    private void Dismiss()
    {
        _dismissedFingerprints.Add(Fingerprint(_availability.Current));
        Refresh();
    }

    private void OnAvailabilityChanged(object? sender, CliAvailabilityState state) => Refresh();

    private static string Fingerprint(CliAvailabilityState state) =>
        $"{state.State}:" + string.Join("|", state.Probes
            .OrderBy(static probe => probe.Cli, StringComparer.OrdinalIgnoreCase)
            .Select(static probe => $"{probe.Cli}:{probe.Detected}:{probe.Minimum}:{probe.RawVersionLine}"));

    private static string BuildHeadline(CliAvailabilityState state)
    {
        var affected = state.Probes.Where(static probe => probe.IsOutdated).ToArray();
        if (state.State == CliAvailability.Available || affected.Length == 0)
        {
            return string.Empty;
        }

        if (state.State == CliAvailability.NotInstalled)
        {
            var missingNames = affected
                .Where(static probe => probe.Detected.Equals(UnknownCliVersion))
                .Select(DisplayName)
                .ToArray();
            return missingNames.Length == 1
                ? $"{missingNames[0]} is not installed or could not be probed."
                : "Required CLI tools are not installed or could not be probed.";
        }

        if (affected.Length == 1)
        {
            var probe = affected[0];
            return $"{DisplayName(probe)} {probe.Detected} is older than the minimum supported ({probe.Minimum}).";
        }

        return "Some CLI tools are older than the minimum supported versions.";
    }

    private static string BuildUpgradeInstructions(IReadOnlyList<CliVersionInfo> probes)
    {
        var lines = new List<string>();
        foreach (var probe in probes.Where(static p => p.IsOutdated).OrderBy(static p => p.Cli, StringComparer.OrdinalIgnoreCase))
        {
            var installedText = probe.Detected.Equals(UnknownCliVersion)
                ? "not detected"
                : $"detected {probe.Detected}";
            lines.Add($"{DisplayName(probe)} ({installedText}; minimum {probe.Minimum})");
            lines.Add($"Run: {UpgradeCommand(probe.Cli)}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string DisplayName(CliVersionInfo probe) =>
        string.Equals(probe.Cli, "gh", StringComparison.OrdinalIgnoreCase)
            ? "GitHub CLI"
            : "Copilot CLI";

    private static string UpgradeCommand(string cli) =>
        string.Equals(cli, "gh", StringComparison.OrdinalIgnoreCase)
            ? "winget upgrade GitHub.cli"
            : "gh extension install github/gh-copilot && gh extension upgrade gh-copilot";
}

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using CopilotSessionManager.Core.Cli;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using CopilotSessionManager.ViewModels;

namespace CopilotSessionManager.Views;

/// <summary>
/// Modal dialog that displays per-model token consumption for a single session.
/// Shows a live tally for active sessions (sums <c>assistant.message.outputTokens</c>
/// and <c>session.compaction_complete.compactionTokensUsed</c>) and the
/// authoritative cumulative totals from <c>session.shutdown.modelMetrics</c>
/// once the session has closed cleanly.
/// </summary>
public partial class SessionTokensWindow : Window
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    private readonly SessionCardViewModel _card;
    private readonly ICopilotCliAdapter _adapter;
    private readonly string _eventsPath;

    public SessionTokensWindow(SessionCardViewModel card, ICopilotCliAdapter adapter, ICopilotPaths paths)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(paths);

        InitializeComponent();

        _card = card;
        _adapter = adapter;
        _eventsPath = Path.Combine(paths.SessionStateDirectory, card.Id, "events.jsonl");

        HeaderSessionText.Text = $"{card.DisplayName} — {card.ShortId}";
        var repoLine = string.IsNullOrWhiteSpace(card.Repository)
            ? "Tokens consumed by this session across all models."
            : $"{card.Repository} · Tokens consumed by this session across all models.";
        HeaderSubText.Text = repoLine;

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        SessionModelInfo info;
        try
        {
            if (!File.Exists(_eventsPath))
            {
                ProvenanceText.Text = $"No events.jsonl file at {_eventsPath}.";
                UsageGrid.ItemsSource = Array.Empty<UsageRow>();
                TotalsText.Text = "No usage data available.";
                return;
            }

            await using var stream = new FileStream(
                _eventsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            info = await _adapter.ReadSessionModelInfoAsync(stream);
        }
        catch (Exception ex)
        {
            ProvenanceText.Text = $"Failed to read events.jsonl: {ex.Message}";
            UsageGrid.ItemsSource = Array.Empty<UsageRow>();
            TotalsText.Text = "No usage data available.";
            return;
        }

        Render(info);
    }

    private void Render(SessionModelInfo info)
    {
        ProvenanceText.Text = info.IsFromShutdown
            ? "Source: session.shutdown.modelMetrics — authoritative final totals reported by the Copilot CLI when this session ended cleanly."
            : "Source: live snapshot — sums assistant.message.outputTokens (per turn) plus session.compaction_complete.compactionTokensUsed (per compaction). Input tokens are only counted at compaction boundaries; totals will grow as the session runs.";

        if (info.UsageByModel is null || info.UsageByModel.Count == 0)
        {
            UsageGrid.ItemsSource = Array.Empty<UsageRow>();
            TotalsText.Text = "No token usage recorded yet for this session.";
            return;
        }

        var rows = info.UsageByModel
            .OrderByDescending(kv => kv.Value.TotalTokens)
            .Select(kv => new UsageRow(kv.Key, kv.Value))
            .ToList();

        UsageGrid.ItemsSource = rows;

        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0, reasoning = 0;
        int requests = 0;
        foreach (var r in rows)
        {
            input += r.Usage.InputTokens;
            output += r.Usage.OutputTokens;
            cacheRead += r.Usage.CacheReadTokens;
            cacheWrite += r.Usage.CacheWriteTokens;
            reasoning += r.Usage.ReasoningTokens;
            requests += r.Usage.RequestCount;
        }
        var total = input + output + cacheRead + cacheWrite + reasoning;

        TotalsText.Text =
            $"Total: {Fmt(total)} tokens across {rows.Count} model(s), {Fmt(requests)} request(s).  " +
            $"Input {Fmt(input)} · Output {Fmt(output)} · Cache read {Fmt(cacheRead)} · Cache write {Fmt(cacheWrite)}" +
            (reasoning > 0 ? $" · Reasoning {Fmt(reasoning)}" : string.Empty);
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await LoadAsync();

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        if (UsageGrid.ItemsSource is not IEnumerable<UsageRow> rows)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Session: {_card.DisplayName} ({_card.ShortId})");
        sb.AppendLine(ProvenanceText.Text);
        sb.AppendLine();
        sb.AppendLine("Model | Requests | Input | Output | Cache read | Cache write | Reasoning");
        foreach (var r in rows)
        {
            sb.AppendLine($"{r.Model} | {r.RequestsDisplay} | {r.InputDisplay} | {r.OutputDisplay} | {r.CacheReadDisplay} | {r.CacheWriteDisplay} | {r.ReasoningDisplay}");
        }
        sb.AppendLine();
        sb.AppendLine(TotalsText.Text);

        try
        {
            Clipboard.SetText(sb.ToString());
        }
        catch
        {
            // Clipboard can occasionally be locked by another process; ignore.
        }
    }

    private static string Fmt(long n) => n.ToString("N0", Culture);

    private sealed class UsageRow
    {
        public UsageRow(string model, ModelUsage usage)
        {
            Model = model;
            Usage = usage;
        }

        public string Model { get; }
        public ModelUsage Usage { get; }

        public string RequestsDisplay => Usage.RequestCount.ToString("N0", Culture);
        public string InputDisplay => Fmt(Usage.InputTokens);
        public string OutputDisplay => Fmt(Usage.OutputTokens);
        public string CacheReadDisplay => Fmt(Usage.CacheReadTokens);
        public string CacheWriteDisplay => Fmt(Usage.CacheWriteTokens);
        public string ReasoningDisplay => Fmt(Usage.ReasoningTokens);
        public string TotalDisplay => Fmt(Usage.TotalTokens);
    }
}

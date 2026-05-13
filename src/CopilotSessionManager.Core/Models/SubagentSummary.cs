using System.Globalization;

namespace CopilotSessionManager.Core.Models;

public enum SubagentStatus
{
    Running,
    Completed,
    Cancelled,
}

public sealed record SubagentSummary(
    string ToolCallId,
    string Name,
    string AgentType,
    string? AgentDisplayName,
    string? Model,
    long TokensTotal,
    int ToolCallsTotal,
    TimeSpan? Duration,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    SubagentStatus Status)
{
    public string TokensDisplay => FormatTokens(TokensTotal);

    public string DurationDisplay
    {
        get
        {
            if (Duration is not { } d)
            {
                return "—";
            }

            if (d.TotalMilliseconds < 1000)
            {
                return $"{Math.Max(1, (int)Math.Round(d.TotalMilliseconds))}ms";
            }
            if (d.TotalMinutes < 1)
            {
                return d.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
            }
            if (d.TotalHours < 1)
            {
                return $"{(int)d.TotalMinutes}m {d.Seconds}s";
            }
            return $"{(int)d.TotalHours}h {d.Minutes}m";
        }
    }

    private static string FormatTokens(long total)
    {
        if (total <= 0)
        {
            return "—";
        }
        if (total < 1000)
        {
            return total.ToString(CultureInfo.InvariantCulture);
        }
        if (total < 10_000)
        {
            return (total / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k";
        }
        if (total < 1_000_000)
        {
            return (total / 1000).ToString(CultureInfo.InvariantCulture) + "k";
        }
        return (total / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M";
    }
}

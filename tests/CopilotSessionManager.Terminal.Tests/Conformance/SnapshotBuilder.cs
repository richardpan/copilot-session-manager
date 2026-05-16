using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CopilotSessionManager.Terminal.Tests.Conformance;

/// <summary>
/// Renders a <see cref="ScreenBuffer"/> + parser event history into a
/// deterministic textual snapshot that the conformance harness can
/// commit and diff. The format is intentionally human-readable so
/// regressions surface as a meaningful textual diff in PR review.
/// </summary>
internal static class SnapshotBuilder
{
    public const string SchemaHeader = "# CapturePtyTrace conformance snapshot v1";

    public static string Build(string traceName, TraceMetadata metadata, ScreenBuffer buffer, IReadOnlyList<VtEvent> events)
    {
        var sb = new StringBuilder();
        sb.AppendLine(SchemaHeader);
        sb.AppendLine($"trace: {traceName}");
        sb.AppendLine($"geometry: {metadata.Columns}x{metadata.Rows}");
        sb.AppendLine($"events: {events.Count}");
        sb.AppendLine($"alt-screen: {buffer.UsingAlternateScreen.ToString().ToLowerInvariant()}");
        sb.AppendLine($"cursor: row={buffer.CursorRow} col={buffer.CursorColumn}");
        sb.AppendLine($"cursor-visible: {buffer.CursorVisible.ToString().ToLowerInvariant()}");
        sb.AppendLine($"scrollback-lines: {buffer.ScrollbackLineCount}");
        sb.AppendLine($"title: {JsonSerializer.Serialize(buffer.WindowTitle)}");

        sb.AppendLine();
        sb.AppendLine("event-counts:");
        foreach (var (name, count) in CountEventsByType(events))
        {
            sb.AppendLine($"  {name}={count}");
        }

        sb.AppendLine();
        sb.AppendLine("viewport:");
        for (var row = 1; row <= buffer.Rows; row++)
        {
            sb.AppendLine($"  {row:D3}: |{RenderRow(buffer, row)}|");
        }

        sb.AppendLine();
        sb.AppendLine("styled-spans:");
        var spans = CollectStyledSpans(buffer);
        if (spans.Count == 0)
        {
            sb.AppendLine("  (none - every cell is default style)");
        }
        else
        {
            foreach (var line in spans)
            {
                sb.AppendLine($"  {line}");
            }
        }

        return sb.ToString();
    }

    private static string RenderRow(ScreenBuffer buffer, int row)
    {
        var sb = new StringBuilder(buffer.Columns);
        for (var col = 1; col <= buffer.Columns; col++)
        {
            var cell = buffer.GetCell(row, col);
            var ch = cell.Glyph.Value;
            sb.Append(ch >= 0x20 && ch < 0x7F ? (char)ch : ch == 0x20 ? ' ' : '.');
        }
        return sb.ToString().TrimEnd(' ');
    }

    private static IEnumerable<(string Name, int Count)> CountEventsByType(IReadOnlyList<VtEvent> events)
    {
        return events
            .GroupBy(e => e.GetType().Name)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key, g.Count()));
    }

    private static List<string> CollectStyledSpans(ScreenBuffer buffer)
    {
        var spans = new List<string>();
        for (var row = 1; row <= buffer.Rows; row++)
        {
            int? spanStart = null;
            TerminalColor lastFg = TerminalColor.Default;
            TerminalColor lastBg = TerminalColor.Default;
            CellAttributes lastAttrs = CellAttributes.None;

            for (var col = 1; col <= buffer.Columns; col++)
            {
                var cell = buffer.GetCell(row, col);
                var isStyled =
                    cell.Foreground != TerminalColor.Default
                    || cell.Background != TerminalColor.Default
                    || cell.Attributes != CellAttributes.None;

                if (isStyled && spanStart is null)
                {
                    spanStart = col;
                    lastFg = cell.Foreground;
                    lastBg = cell.Background;
                    lastAttrs = cell.Attributes;
                }
                else if (isStyled && (cell.Foreground != lastFg || cell.Background != lastBg || cell.Attributes != lastAttrs))
                {
                    spans.Add(FormatSpan(row, spanStart!.Value, col - 1, lastFg, lastBg, lastAttrs));
                    spanStart = col;
                    lastFg = cell.Foreground;
                    lastBg = cell.Background;
                    lastAttrs = cell.Attributes;
                }
                else if (!isStyled && spanStart is not null)
                {
                    spans.Add(FormatSpan(row, spanStart.Value, col - 1, lastFg, lastBg, lastAttrs));
                    spanStart = null;
                }
            }

            if (spanStart is not null)
            {
                spans.Add(FormatSpan(row, spanStart.Value, buffer.Columns, lastFg, lastBg, lastAttrs));
            }
        }
        return spans;
    }

    private static string FormatSpan(int row, int colStart, int colEnd, TerminalColor fg, TerminalColor bg, CellAttributes attrs)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:D3},{1:D3}..{2:D3} fg={3} bg={4} attrs={5}",
            row, colStart, colEnd, fg, bg, attrs);
    }
}

internal sealed record TraceMetadata(string CommandLine, short Columns, short Rows);

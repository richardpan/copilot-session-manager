using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// V1.5 (#198): reference <see cref="ISessionDocsSectionProvider"/> that emits
/// a small "Session info" panel at the bottom of every generated
/// <c>SESSION-DOCS.html</c>. Exercises the provider contract end-to-end so
/// the plumbing has a real customer; richer first-party providers (git
/// status, cost rollup, …) can land later as their own services.
/// </summary>
public sealed class SessionMetadataSectionProvider : ISessionDocsSectionProvider
{
    public string Name => "session-info";
    public int Order => 0;

    public ValueTask<IReadOnlyList<DocsSection>> GetSectionsAsync(Session session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var body = BuildBody(session);
        var section = new DocsSection(
            Anchor: "session-info",
            Title: "Session info",
            HtmlBody: body,
            Placement: SectionPlacement.End,
            Subtitle: "Auto-derived from session metadata");

        IReadOnlyList<DocsSection> result = new[] { section };
        return ValueTask.FromResult(result);
    }

    private static string BuildBody(Session session)
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("<dl class=\"session-info\">");

        Row(sb, "Id", session.Id);
        Row(sb, "Host", session.HostType);
        Row(sb, "Producer", session.Producer);
        Row(sb, "Status", session.Status.ToString());
        Row(sb, "Repository", session.Repository);
        Row(sb, "Branch", session.Branch);
        Row(sb, "Cwd", session.Cwd);
        Row(sb, "Created", FormatTimestamp(session.CreatedAt));
        Row(sb, "Updated", FormatTimestamp(session.UpdatedAt));
        Row(sb, "Turns", session.TurnCount.ToString(CultureInfo.InvariantCulture));

        if (!session.CopilotVersion.Equals(CopilotVersion.Zero))
        {
            Row(sb, "CLI version", session.CopilotVersion.ToString());
        }

        sb.AppendLine("</dl>");
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        sb.Append("<dt>").Append(WebUtility.HtmlEncode(label)).Append("</dt>");
        sb.Append("<dd>").Append(WebUtility.HtmlEncode(value)).AppendLine("</dd>");
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}

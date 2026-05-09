using System.Text.RegularExpressions;
using CopilotSessionManager.Core.Models;

namespace CopilotSessionManager.Core.GitHub;

/// <summary>
/// Default <see cref="IGitHubLinkResolver"/>. Constructs <c>https://github.com/</c>
/// URLs from <c>owner/name</c> repository slugs. Returns nulls when the slug
/// is missing or doesn't look like a GitHub repository.
/// </summary>
public sealed class GitHubLinkResolver : IGitHubLinkResolver
{
    private const string GitHubBase = "https://github.com";

    // owner/name where each segment is a typical GitHub identifier
    // (letters, digits, dot, dash, underscore).
    private static readonly Regex SlugPattern = new(
        @"^[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?/[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?$",
        RegexOptions.Compiled);

    public SessionGitHubLinks Resolve(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var slug = NormalizeSlug(session.Repository);
        if (slug is null)
        {
            return SessionGitHubLinks.Empty;
        }

        var repoUrl = $"{GitHubBase}/{slug}";
        var branch = session.Branch?.Trim();
        var branchUrl = string.IsNullOrEmpty(branch)
            ? null
            : $"{repoUrl}/tree/{Uri.EscapeDataString(branch)}";

        return new SessionGitHubLinks(repoUrl, branchUrl, PullRequest: null);
    }

    /// <summary>
    /// Accepts <c>owner/name</c>, <c>https://github.com/owner/name(.git)?</c>,
    /// or <c>git@github.com:owner/name(.git)?</c>; returns the canonical
    /// <c>owner/name</c> form, or null if it doesn't look like GitHub.
    /// </summary>
    private static string? NormalizeSlug(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var s = raw.Trim();

        const string httpsPrefix = "https://github.com/";
        const string httpPrefix = "http://github.com/";
        const string sshPrefix = "git@github.com:";

        if (s.StartsWith(httpsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            s = s[httpsPrefix.Length..];
        }
        else if (s.StartsWith(httpPrefix, StringComparison.OrdinalIgnoreCase))
        {
            s = s[httpPrefix.Length..];
        }
        else if (s.StartsWith(sshPrefix, StringComparison.OrdinalIgnoreCase))
        {
            s = s[sshPrefix.Length..];
        }

        if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^4];
        }

        s = s.TrimEnd('/');

        return SlugPattern.IsMatch(s) ? s : null;
    }
}

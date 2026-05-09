using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CopilotSessionManager.Core.GitHub.Issues;

/// <summary>
/// Tolerant parser for the strings the user can type into the "+ Issue"
/// dialog. Accepts:
/// <list type="bullet">
///   <item><c>owner/repo#NN</c></item>
///   <item><c>#NN</c> or <c>NN</c> (resolved against the session's repo)</item>
///   <item><c>https://github.com/owner/repo/issues/NN</c></item>
/// </list>
/// PR URLs (<c>/pull/NN</c>) are explicitly rejected because the manual
/// linking surface is for issues only.
/// </summary>
public static class IssueRefParser
{
    // GitHub repo segment rules: 1..39 chars, alphanumeric plus '-', '_',
    // '.', and may not start with '-'. Owner has the same shape in practice
    // (organisation / user names). We deliberately don't enforce GitHub's
    // dotted-edge rules — that's a server-side concern; we just want to
    // bounce obviously-bogus input before shelling out.
    private const string SegmentPattern = @"[A-Za-z0-9_\.][A-Za-z0-9_\.\-]{0,38}";
    private const string OwnerRepoPattern = SegmentPattern + "/" + SegmentPattern;

    private static readonly Regex OwnerRepoRegex = new(
        "^" + OwnerRepoPattern + "$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex OwnerRepoHashRegex = new(
        "^(?<repo>" + OwnerRepoPattern + ")#(?<num>\\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BareNumberRegex = new(
        @"^#?(?<num>\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IssueUrlRegex = new(
        @"^https?://(?:www\.)?github\.com/(?<repo>" + OwnerRepoPattern + @")/issues/(?<num>\d+)(?:[/?#].*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex PullUrlRegex = new(
        @"^https?://(?:www\.)?github\.com/" + OwnerRepoPattern + @"/pull/\d+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses <paramref name="input"/> into an <see cref="IssueRef"/>.
    /// Returns <c>true</c> on success and writes the canonical ref to
    /// <paramref name="issueRef"/>; returns <c>false</c> for empty,
    /// whitespace, malformed input, non-positive numbers, and PR URLs.
    /// </summary>
    /// <param name="input">User-supplied text from the dialog.</param>
    /// <param name="defaultOwnerRepo">
    /// The session's <c>owner/repo</c> slug, used when only <c>#NN</c> /
    /// <c>NN</c> is supplied. May be <c>null</c>; in that case bare-number
    /// inputs return <c>false</c>.
    /// </param>
    public static bool TryParse(string? input, string? defaultOwnerRepo, [NotNullWhen(true)] out IssueRef? issueRef)
    {
        issueRef = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();

        // Reject pull-request URLs explicitly — this dialog is for issues.
        if (PullUrlRegex.IsMatch(trimmed))
        {
            return false;
        }

        // Full URL form first.
        var urlMatch = IssueUrlRegex.Match(trimmed);
        if (urlMatch.Success)
        {
            return TryBuild(urlMatch.Groups["repo"].Value, urlMatch.Groups["num"].Value, out issueRef);
        }

        // owner/repo#NN form.
        var hashMatch = OwnerRepoHashRegex.Match(trimmed);
        if (hashMatch.Success)
        {
            return TryBuild(hashMatch.Groups["repo"].Value, hashMatch.Groups["num"].Value, out issueRef);
        }

        // Bare #NN or NN — needs a default owner/repo.
        var bareMatch = BareNumberRegex.Match(trimmed);
        if (bareMatch.Success)
        {
            if (string.IsNullOrWhiteSpace(defaultOwnerRepo))
            {
                return false;
            }
            var defaultSlug = defaultOwnerRepo.Trim();
            if (!OwnerRepoRegex.IsMatch(defaultSlug))
            {
                return false;
            }
            return TryBuild(defaultSlug, bareMatch.Groups["num"].Value, out issueRef);
        }

        return false;
    }

    private static bool TryBuild(string repo, string number, [NotNullWhen(true)] out IssueRef? issueRef)
    {
        issueRef = null;
        if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0)
        {
            return false;
        }
        if (!OwnerRepoRegex.IsMatch(repo))
        {
            return false;
        }
        issueRef = new IssueRef(repo, n);
        return true;
    }
}

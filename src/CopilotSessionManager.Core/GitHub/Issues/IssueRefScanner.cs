using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CopilotSessionManager.Core.GitHub.Issues;

/// <summary>
/// Pure, headless scanner that picks GitHub issue references out of a
/// session's <c>SESSION-README.md</c>. Recognises two shapes:
/// <list type="bullet">
///   <item><c>owner/repo#NN</c> — fully qualified cross-repo refs.</item>
///   <item><c>#NN</c> — bare refs, resolved against the supplied
///         <c>defaultOwnerRepo</c>.</item>
/// </list>
/// Markdown link forms (<c>[#42](https://github.com/o/r/issues/42)</c>) are
/// caught by the URL match inside the link target. Headings (<c>## Foo</c>),
/// inline code (<c>`gh pr list #42`</c>), fenced code blocks, and URL
/// fragments (<c>https://x/y#frag</c>) are filtered out before matching.
/// Pure function — no I/O, no logging.
/// </summary>
public static class IssueRefScanner
{
    /// <summary>Hard cap on the number of refs returned to defend against pathological input.</summary>
    public const int MaxRefs = 50;

    // Same shape as IssueRefParser.SegmentPattern. Kept private here so the
    // scanner stays self-contained even though it duplicates the literal.
    private const string SegmentPattern = @"[A-Za-z0-9_\.][A-Za-z0-9_\.\-]{0,38}";
    private const string OwnerRepoPattern = SegmentPattern + "/" + SegmentPattern;

    // Owner/repo#NN — fully qualified.
    private static readonly Regex OwnerRepoRefRegex = new(
        @"(?<![A-Za-z0-9_\.\-/])(?<repo>" + OwnerRepoPattern + @")#(?<num>\d+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Bare #NN — must be at a word boundary on both sides and must not be
    // preceded by another '#' (so "##" headings cannot match) or by '/'
    // (so URL fragments don't match the digits before them).
    private static readonly Regex BareRefRegex = new(
        @"(?<![A-Za-z0-9_\-#/])#(?<num>\d+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // GitHub issue URLs — these resolve to the explicit owner/repo from the
    // URL, regardless of any default.
    private static readonly Regex IssueUrlRegex = new(
        @"https?://(?:www\.)?github\.com/(?<repo>" + OwnerRepoPattern + @")/issues/(?<num>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Scans <paramref name="markdown"/> for issue refs.
    /// </summary>
    /// <param name="markdown">README contents. May be <c>null</c> or empty.</param>
    /// <param name="defaultOwnerRepo">
    /// Owner/repo to resolve bare <c>#NN</c> refs against. When <c>null</c> /
    /// blank, bare refs are skipped (cannot be resolved).
    /// </param>
    /// <returns>
    /// Deduplicated refs in first-occurrence order, capped at
    /// <see cref="MaxRefs"/>. Empty for null/blank input.
    /// </returns>
    public static IReadOnlyList<IssueRef> Scan(string? markdown, string? defaultOwnerRepo)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return Array.Empty<IssueRef>();
        }

        var sanitised = StripCodeAndFragments(markdown);
        BlankIssueLinkLabels(sanitised.ToCharArray(), out sanitised);

        var defaultSlug = string.IsNullOrWhiteSpace(defaultOwnerRepo) ? null : defaultOwnerRepo!.Trim();

        // Collect (position, ref) pairs so we can sort by document order
        // before deduplication.
        var hits = new List<(int Index, IssueRef Ref)>();

        foreach (Match m in IssueUrlRegex.Matches(sanitised))
        {
            if (TryBuild(m.Groups["repo"].Value, m.Groups["num"].Value, out var r))
            {
                hits.Add((m.Index, r!));
            }
        }

        foreach (Match m in OwnerRepoRefRegex.Matches(sanitised))
        {
            if (TryBuild(m.Groups["repo"].Value, m.Groups["num"].Value, out var r))
            {
                hits.Add((m.Index, r!));
            }
        }

        if (!string.IsNullOrEmpty(defaultSlug))
        {
            foreach (Match m in BareRefRegex.Matches(sanitised))
            {
                if (TryBuild(defaultSlug!, m.Groups["num"].Value, out var r))
                {
                    hits.Add((m.Index, r!));
                }
            }
        }

        if (hits.Count == 0)
        {
            return Array.Empty<IssueRef>();
        }

        hits.Sort(static (a, b) => a.Index.CompareTo(b.Index));

        var seen = new HashSet<IssueRef>();
        var result = new List<IssueRef>(Math.Min(hits.Count, MaxRefs));
        foreach (var (_, r) in hits)
        {
            if (seen.Add(r))
            {
                result.Add(r);
                if (result.Count >= MaxRefs)
                {
                    break;
                }
            }
        }

        return result;
    }

    private static bool TryBuild(string repo, string number, out IssueRef? issueRef)
    {
        issueRef = null;
        if (!int.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) || n <= 0)
        {
            return false;
        }
        try
        {
            issueRef = new IssueRef(repo, n);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Replaces fenced code blocks, inline code spans, and URL fragments
    /// with whitespace of equivalent length so character offsets stay
    /// stable but contents are no longer matchable. Preserving offsets
    /// keeps document-order sorting honest.
    /// </summary>
    private static string StripCodeAndFragments(string text)
    {
        var chars = text.ToCharArray();
        int i = 0;

        while (i < chars.Length)
        {
            // Fenced code block — three backticks at line start (allow leading whitespace).
            if (IsFenceAt(chars, i))
            {
                var fenceStart = i;
                // Skip the opening fence line entirely.
                i = SkipToLineEnd(chars, i);
                // Blank everything until the next closing fence on its own line.
                while (i < chars.Length && !IsFenceAt(chars, i))
                {
                    if (chars[i] != '\r' && chars[i] != '\n')
                    {
                        chars[i] = ' ';
                    }
                    i++;
                }
                // Blank the closing fence line if present.
                if (i < chars.Length)
                {
                    var closeEnd = SkipToLineEnd(chars, i);
                    for (var k = i; k < closeEnd; k++)
                    {
                        if (chars[k] != '\r' && chars[k] != '\n')
                        {
                            chars[k] = ' ';
                        }
                    }
                    i = closeEnd;
                }
                _ = fenceStart;
                continue;
            }

            // Inline code span — single backtick…backtick on the same line.
            if (chars[i] == '`')
            {
                var end = i + 1;
                while (end < chars.Length && chars[end] != '`' && chars[end] != '\n')
                {
                    end++;
                }
                if (end < chars.Length && chars[end] == '`')
                {
                    for (var k = i; k <= end; k++)
                    {
                        chars[k] = ' ';
                    }
                    i = end + 1;
                    continue;
                }
            }

            i++;
        }

        // Strip URL fragments: from a '#' that's preceded by a non-whitespace
        // character all the way until whitespace, in URL-looking contexts.
        // We do this by detecting URL prefixes and blanking '#fragment' after
        // an '/issues/NN' or generic URL.
        BlankUrlFragments(chars);

        return new string(chars);
    }

    private static void BlankUrlFragments(char[] chars)
    {
        // Find http(s):// occurrences and skip ahead to a '#' that is not
        // preceded by whitespace, then blank from '#' until whitespace.
        var s = new string(chars);
        var idx = 0;
        while (idx < s.Length)
        {
            var prot = s.IndexOf("http", idx, StringComparison.OrdinalIgnoreCase);
            if (prot < 0)
            {
                break;
            }
            // Find end of URL token (whitespace, ')', '"', or end).
            var end = prot;
            while (end < s.Length && !IsUrlTerminator(s[end]))
            {
                end++;
            }
            // Within [prot, end), blank any '#' that follows a non-'/' char.
            for (var k = prot; k < end; k++)
            {
                if (chars[k] == '#')
                {
                    // Blank the '#' and the digits/identifier directly after.
                    var j = k;
                    while (j < end && chars[j] != ' ' && chars[j] != '\t')
                    {
                        chars[j] = ' ';
                        j++;
                    }
                    break;
                }
            }
            idx = end;
        }
    }

    private static bool IsUrlTerminator(char c) =>
        c is ' ' or '\t' or '\n' or '\r' or ')' or '"' or '\'' or '<' or '>' or '`';

    /// <summary>
    /// In a markdown link of the form <c>[label](url)</c> where <c>url</c>
    /// is a GitHub issue URL, blank the label so the URL form wins on
    /// dedup. Without this, <c>[#42](https://github.com/acme/tools/issues/42)</c>
    /// would emit two distinct refs (one bare against the default repo, one
    /// from the URL).
    /// </summary>
    private static void BlankIssueLinkLabels(char[] chars, out string result)
    {
        // Walk through and look for "](" then back-track to find the matching
        // '['. If the URL inside the parens parses as a github issue URL,
        // blank the [label] span.
        for (var i = 0; i < chars.Length - 1; i++)
        {
            if (chars[i] != ']' || chars[i + 1] != '(')
            {
                continue;
            }
            // Find matching '[' before i.
            var open = -1;
            for (var k = i - 1; k >= 0; k--)
            {
                if (chars[k] == '[')
                {
                    open = k;
                    break;
                }
                if (chars[k] == '\n')
                {
                    break;
                }
            }
            if (open < 0)
            {
                continue;
            }
            // Find closing ')'.
            var urlStart = i + 2;
            var urlEnd = urlStart;
            while (urlEnd < chars.Length && chars[urlEnd] != ')' && chars[urlEnd] != '\n')
            {
                urlEnd++;
            }
            if (urlEnd >= chars.Length || chars[urlEnd] != ')')
            {
                continue;
            }
            var url = new string(chars, urlStart, urlEnd - urlStart);
            if (IssueUrlRegex.IsMatch(url))
            {
                // Blank label characters between [ and ] inclusive.
                for (var k = open; k <= i; k++)
                {
                    chars[k] = ' ';
                }
            }
        }

        result = new string(chars);
    }

    private static bool IsFenceAt(char[] chars, int i)
    {
        // Allow leading whitespace on the line.
        var lineStart = i;
        while (lineStart > 0 && chars[lineStart - 1] != '\n')
        {
            lineStart--;
        }
        var p = lineStart;
        while (p < chars.Length && (chars[p] == ' ' || chars[p] == '\t'))
        {
            p++;
        }
        // Only treat as fence when we are AT the first non-whitespace char of the line.
        if (p != i)
        {
            return false;
        }
        return i + 2 < chars.Length && chars[i] == '`' && chars[i + 1] == '`' && chars[i + 2] == '`';
    }

    private static int SkipToLineEnd(char[] chars, int i)
    {
        while (i < chars.Length && chars[i] != '\n')
        {
            i++;
        }
        if (i < chars.Length)
        {
            i++; // include the '\n'
        }
        return i;
    }
}

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CopilotSessionManager.Core.Logging;

/// <summary>
/// Pure, dependency-free PII / secret scrubber used by the Serilog enricher
/// and any other surface that wants to render user-supplied or process-supplied
/// strings into a log. The intent is "safe by default": prefer over-redaction
/// to leakage. All replacements are done in-place on the string and never
/// throw — a bad regex match still returns the original input.
/// </summary>
public static partial class LogRedaction
{
    /// <summary>The token used to replace any matched secret-shaped substring.</summary>
    public const string Placeholder = "[REDACTED]";

    /// <summary>
    /// Property names whose <c>ScalarValue</c> should be replaced with
    /// <see cref="Placeholder"/> regardless of content. Compared
    /// case-insensitively after stripping non-alphanumerics so
    /// <c>api_key</c>, <c>API-KEY</c>, and <c>apiKey</c> all match.
    /// </summary>
    public static readonly IReadOnlySet<string> SensitivePropertyNames = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "prompt",
        "transcript",
        "content",
        "body",
        "messagetext",
        "token",
        "accesstoken",
        "refreshtoken",
        "secret",
        "password",
        "passwd",
        "apikey",
        "authorization",
        "auth",
        "ghtoken",
        "githubtoken",
        "openaikey",
        "anthropickey",
    };

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="propertyName"/> matches
    /// a known sensitive property name. Matching is case-insensitive and ignores
    /// underscores, dashes, and dots so <c>api_key</c>, <c>API-KEY</c>, and
    /// <c>apiKey</c> all map to <c>"apikey"</c>.
    /// </summary>
    public static bool IsSensitivePropertyName(string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
            return false;
        var normalized = NormalizePropertyName(propertyName);
        return SensitivePropertyNames.Contains(normalized);
    }

    /// <summary>
    /// Scrub a free-form string. Tokens that match a known shape (GitHub PATs,
    /// Slack tokens, JWTs, OpenAI keys, AWS access keys, generic
    /// <c>password=…</c> / <c>secret=…</c> / <c>token=…</c> assignments,
    /// <c>Authorization: Bearer …</c> headers) are replaced with
    /// <see cref="Placeholder"/>. Returns the input unchanged if it is null,
    /// empty, or has no matches.
    /// </summary>
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        try
        {
            var s = value;
            s = GitHubTokenRegex().Replace(s, Placeholder);
            s = JwtRegex().Replace(s, Placeholder);
            s = OpenAiKeyRegex().Replace(s, Placeholder);
            s = AnthropicKeyRegex().Replace(s, Placeholder);
            s = AwsAccessKeyRegex().Replace(s, Placeholder);
            s = SlackTokenRegex().Replace(s, Placeholder);
            s = BearerHeaderRegex().Replace(s, $"Bearer {Placeholder}");
            s = AssignmentRegex().Replace(s, m => $"{m.Groups["key"].Value}{m.Groups["sep"].Value}{Placeholder}");
            return s;
        }
        catch (RegexMatchTimeoutException)
        {
            // Defensive: never let redaction failures surface to the caller.
            return value;
        }
    }

    private static string NormalizePropertyName(string propertyName)
    {
        Span<char> buf = stackalloc char[propertyName.Length];
        var j = 0;
        foreach (var c in propertyName)
        {
            if (char.IsLetterOrDigit(c))
                buf[j++] = char.ToLowerInvariant(c);
        }
        return new string(buf[..j]);
    }

    // gh[ps]_<36>, github_pat_<22>_<59>
    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{22,}_[A-Za-z0-9_]{40,})\b", RegexOptions.Compiled)]
    private static partial Regex GitHubTokenRegex();

    // Three base64url segments separated by dots
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b", RegexOptions.Compiled)]
    private static partial Regex JwtRegex();

    // OpenAI: sk-... (project keys may have sk-proj- prefix)
    [GeneratedRegex(@"\bsk-(?:proj-)?[A-Za-z0-9_-]{20,}\b", RegexOptions.Compiled)]
    private static partial Regex OpenAiKeyRegex();

    // Anthropic API keys
    [GeneratedRegex(@"\bsk-ant-[A-Za-z0-9_-]{20,}\b", RegexOptions.Compiled)]
    private static partial Regex AnthropicKeyRegex();

    // AWS access key IDs (AKIA / ASIA / AGPA / AROA / AIDA + 16 alphanum)
    [GeneratedRegex(@"\b(?:AKIA|ASIA|AGPA|AROA|AIDA|ANPA|ANVA|ABIA|ACCA)[0-9A-Z]{16}\b", RegexOptions.Compiled)]
    private static partial Regex AwsAccessKeyRegex();

    // Slack tokens: xox[bopas]-...
    [GeneratedRegex(@"\bxox[bopasr]-[A-Za-z0-9-]{10,}\b", RegexOptions.Compiled)]
    private static partial Regex SlackTokenRegex();

    // Authorization: Bearer <token>
    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9._~+/-]{16,}=*", RegexOptions.Compiled)]
    private static partial Regex BearerHeaderRegex();

    // password=..., token=..., secret=..., apikey=..., api_key=..., access_token=...
    // Stops at whitespace, comma, semicolon, ampersand, or closing quote.
    [GeneratedRegex(
        @"(?ix)
        (?<key>(?:password|passwd|secret|token|apikey|api[_-]?key|access[_-]?token|refresh[_-]?token))
        (?<sep>\s*[:=]\s*""?)
        (?<val>[^\s,;&""]{4,})",
        RegexOptions.Compiled)]
    private static partial Regex AssignmentRegex();
}

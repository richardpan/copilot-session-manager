using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using CopilotSessionManager.Core.Models;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// Substitutes a small set of <see cref="Session"/>-derived placeholders
/// into the user-configured wrap-up prompt template (#149).
/// </summary>
/// <remarks>
/// Recognised tokens: <c>{sessionId}</c>, <c>{summary}</c>,
/// <c>{repository}</c>, <c>{branch}</c>. Null / whitespace fields render
/// as <c>(unknown)</c>. Any unrecognised <c>{placeholder}</c> is left
/// literal so that a typo in user config does not crash the launcher.
/// </remarks>
public static class WrapUpPromptBuilder
{
    private const string Unknown = "(unknown)";

    private static readonly Regex TokenPattern = new(
        @"\{(?<name>[A-Za-z][A-Za-z0-9_]*)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Build(string template, Session session)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(session);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sessionId"] = NullIfWhitespace(session.Id),
            ["summary"] = NullIfWhitespace(session.Summary),
            ["repository"] = NullIfWhitespace(session.Repository),
            ["branch"] = NullIfWhitespace(session.Branch),
        };

        return TokenPattern.Replace(template, m =>
        {
            var name = m.Groups["name"].Value;
            return values.TryGetValue(name, out var v) ? v : m.Value;
        });
    }

    private static string NullIfWhitespace(string? value)
        => string.IsNullOrWhiteSpace(value) ? Unknown : value!;
}

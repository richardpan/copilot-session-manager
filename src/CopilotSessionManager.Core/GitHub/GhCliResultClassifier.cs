using CopilotSessionManager.Core.Onboarding;

namespace CopilotSessionManager.Core.GitHub;

/// <summary>
/// Pure helper that maps a <see cref="ProcessRunResult"/> from a <c>gh</c>
/// CLI invocation onto a <see cref="GitHubAvailability"/> classification +
/// user-friendly message. Deliberately stateless and side-effect free so it
/// can be unit-tested directly.
/// </summary>
public static class GhCliResultClassifier
{
    /// <summary>Substrings (case-insensitive) that indicate a network failure.</summary>
    private static readonly string[] NetworkMarkers =
    {
        "could not resolve host",
        "network is unreachable",
        "no such host",
        "tls handshake",
        "temporary failure in name resolution",
        "connection refused",
        "connection reset",
        "no route to host",
        "i/o timeout",
        "request timed out",
        "timed out",
        "dial tcp",
        "lookup ",
        "eai_again",
    };

    /// <summary>Substrings (case-insensitive) that indicate the CLI isn't logged in.</summary>
    private static readonly string[] AuthMarkers =
    {
        "not authenticated",
        "gh auth login",
        "gh auth status",
        "authentication required",
        "you are not logged into",
        "401",
        "bad credentials",
        "requires authentication",
    };

    /// <summary>
    /// Classify a completed <c>gh</c> invocation. Successful exits are always
    /// <see cref="GitHubAvailability.Available"/>. Non-zero exits are scanned
    /// for known network / auth markers in stderr (and stdout, since some
    /// gh subcommands print errors there).
    /// </summary>
    public static (GitHubAvailability State, string? Message) Classify(ProcessRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.ExitCode == 0)
        {
            return (GitHubAvailability.Available, null);
        }

        var combined = (result.StdErr + "\n" + result.StdOut);
        var lower = combined.ToLowerInvariant();

        if (ContainsAny(lower, NetworkMarkers))
        {
            return (
                GitHubAvailability.Offline,
                "GitHub appears to be offline. Network-dependent features (PR sync, branch lookups) are paused — they'll auto-recover when connectivity returns.");
        }

        if (ContainsAny(lower, AuthMarkers))
        {
            return (
                GitHubAvailability.Unauthenticated,
                "GitHub CLI isn't signed in. Run \"gh auth login\" in a terminal to enable PR and branch features.");
        }

        // Unknown failure. Don't change availability — we can't tell whether
        // it was transient. Caller will skip the Report.
        return (GitHubAvailability.Available, null);
    }

    /// <summary>
    /// True when any of <paramref name="markers"/> appear in the (already
    /// lowercased) <paramref name="haystack"/>.
    /// </summary>
    public static bool ContainsAny(string haystack, IReadOnlyList<string> markers)
    {
        ArgumentNullException.ThrowIfNull(haystack);
        ArgumentNullException.ThrowIfNull(markers);

        for (var i = 0; i < markers.Count; i++)
        {
            if (haystack.Contains(markers[i], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

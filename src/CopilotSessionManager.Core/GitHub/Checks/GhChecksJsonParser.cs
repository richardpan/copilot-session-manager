using System;
using System.Collections.Generic;
using System.Text.Json;

namespace CopilotSessionManager.Core.GitHub.Checks;

/// <summary>
/// Parses the JSON output of
/// <c>gh pr checks &lt;number&gt; --json name,state,bucket</c> into a
/// <see cref="PullRequestCheckSummary"/>. Public + static so the parser is
/// unit-testable without spawning <c>gh</c>.
/// </summary>
public static class GhChecksJsonParser
{
    /// <summary>
    /// Parses a <c>gh pr checks</c> JSON array into a rollup. Returns a
    /// <see cref="PullRequestCheckSummary"/> with
    /// <see cref="PullRequestCheckRollup.None"/> when the array is empty;
    /// returns <c>null</c> only when the payload is so malformed it cannot
    /// be parsed at all.
    /// </summary>
    public static PullRequestCheckSummary? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            if (doc.RootElement.GetArrayLength() == 0)
            {
                return new PullRequestCheckSummary(PullRequestCheckRollup.None, Array.Empty<string>());
            }

            var hasFailure = false;
            var hasPending = false;
            var hasSuccess = false;
            var attention = new List<string>();

            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var bucket = entry.TryGetProperty("bucket", out var bucketEl) && bucketEl.ValueKind == JsonValueKind.String
                    ? bucketEl.GetString()
                    : null;
                var state = entry.TryGetProperty("state", out var stateEl) && stateEl.ValueKind == JsonValueKind.String
                    ? stateEl.GetString()
                    : null;
                var name = entry.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                    ? nameEl.GetString()
                    : null;

                var classification = ClassifyOne(bucket, state);
                switch (classification)
                {
                    case PullRequestCheckRollup.Failure:
                        hasFailure = true;
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            attention.Add(name);
                        }
                        break;
                    case PullRequestCheckRollup.Pending:
                        hasPending = true;
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            attention.Add(name);
                        }
                        break;
                    case PullRequestCheckRollup.Success:
                        hasSuccess = true;
                        break;
                }
            }

            // Failure dominates pending dominates success — same precedence
            // GitHub's PR header uses for the rollup pill.
            var rollup = hasFailure ? PullRequestCheckRollup.Failure
                : hasPending ? PullRequestCheckRollup.Pending
                : hasSuccess ? PullRequestCheckRollup.Success
                : PullRequestCheckRollup.None;

            // Don't surface "attention" names when everything passed — there
            // are none anyway, but be explicit.
            if (rollup == PullRequestCheckRollup.Success || rollup == PullRequestCheckRollup.None)
            {
                attention.Clear();
            }

            return new PullRequestCheckSummary(rollup, attention);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PullRequestCheckRollup ClassifyOne(string? bucket, string? state)
    {
        // gh's `bucket` is the most stable surface — it's already a coarse
        // category. Fall back to `state` if `bucket` is missing on older
        // gh versions.
        if (string.IsNullOrWhiteSpace(bucket))
        {
            bucket = state;
        }
        if (string.IsNullOrWhiteSpace(bucket))
        {
            return PullRequestCheckRollup.None;
        }

        var b = bucket!.ToLowerInvariant();
        return b switch
        {
            "fail" or "failure" or "cancel" or "cancelled" or "canceled"
                or "action_required" or "stale" or "timeout" or "timed_out"
                or "error" or "startup_failure"
                => PullRequestCheckRollup.Failure,

            "pending" or "queued" or "in_progress" or "waiting" or "requested"
                => PullRequestCheckRollup.Pending,

            "pass" or "passed" or "success" or "neutral" or "skipping" or "skipped"
                => PullRequestCheckRollup.Success,

            _ => PullRequestCheckRollup.None,
        };
    }
}

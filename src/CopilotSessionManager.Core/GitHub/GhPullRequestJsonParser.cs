using System.Text.Json;
using CopilotSessionManager.Core.Models;

namespace CopilotSessionManager.Core.GitHub;

/// <summary>
/// Parses the JSON output of
/// <c>gh pr list --json number,title,state,isDraft,url</c> into a single
/// <see cref="PullRequestInfo"/>. Public + static so that we can unit-test
/// the parser without spawning <c>gh</c>.
/// </summary>
public static class GhPullRequestJsonParser
{
    /// <summary>
    /// Parses a <c>gh pr list</c> JSON array. Returns the first entry, or
    /// <c>null</c> when the array is empty / the payload is malformed.
    /// </summary>
    public static PullRequestInfo? ParseFirst(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            var first = doc.RootElement[0];
            if (!first.TryGetProperty("number", out var numberEl) || !numberEl.TryGetInt32(out var number))
            {
                return null;
            }

            var title = first.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String
                ? titleEl.GetString() ?? string.Empty
                : string.Empty;

            var url = first.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String
                ? urlEl.GetString() ?? string.Empty
                : string.Empty;

            var stateRaw = first.TryGetProperty("state", out var stateEl) && stateEl.ValueKind == JsonValueKind.String
                ? stateEl.GetString()
                : null;

            var isDraft = first.TryGetProperty("isDraft", out var draftEl)
                && draftEl.ValueKind == JsonValueKind.True;

            var state = MapState(stateRaw, isDraft);
            return new PullRequestInfo(number, title, state, url);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PullRequestState MapState(string? rawState, bool isDraft)
    {
        // gh emits "OPEN" | "CLOSED" | "MERGED" (uppercase).
        if (string.Equals(rawState, "MERGED", StringComparison.OrdinalIgnoreCase))
        {
            return PullRequestState.Merged;
        }
        if (string.Equals(rawState, "CLOSED", StringComparison.OrdinalIgnoreCase))
        {
            return PullRequestState.Closed;
        }
        if (string.Equals(rawState, "OPEN", StringComparison.OrdinalIgnoreCase))
        {
            return isDraft ? PullRequestState.Draft : PullRequestState.Open;
        }
        return PullRequestState.Unknown;
    }
}

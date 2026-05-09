using System;

namespace CopilotSessionManager.Core.GitHub.Issues;

/// <summary>
/// Canonical reference to a single GitHub issue: a lower-cased
/// <c>owner/repo</c> slug and a positive issue number. Equality is
/// structural so refs can be deduped in collections.
/// </summary>
public sealed record IssueRef
{
    /// <summary>
    /// Canonical owner/repo slug, always lower-cased.
    /// </summary>
    public string OwnerRepo { get; }

    /// <summary>
    /// Issue number; always positive.
    /// </summary>
    public int Number { get; }

    public IssueRef(string ownerRepo, int number)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerRepo);
        if (number <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(number), number, "Issue numbers must be positive.");
        }

        OwnerRepo = ownerRepo.Trim().ToLowerInvariant();
        Number = number;
    }

    /// <summary>Renders as <c>owner/repo#NN</c>.</summary>
    public override string ToString() => $"{OwnerRepo}#{Number.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>Canonical web URL for the issue.</summary>
    public string ToCanonicalUrl() =>
        $"https://github.com/{OwnerRepo}/issues/{Number.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}

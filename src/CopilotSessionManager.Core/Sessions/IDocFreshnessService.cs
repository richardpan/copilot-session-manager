using System;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// V1.3 (#147): Computes the freshness of a session's persisted docs
/// (<c>SESSION-README.md</c> / <c>SESSION-DOCS.md</c>) for the "Docs"
/// column badge. Pure, synchronous, side-effect free — safe to call from
/// view-model property getters.
/// </summary>
public interface IDocFreshnessService
{
    /// <summary>
    /// Evaluates freshness for <paramref name="sessionId"/> given that the
    /// session was created at <paramref name="sessionCreatedAt"/>.
    /// </summary>
    /// <returns>
    /// The freshness state plus, for <see cref="DocFreshnessState.Stale"/>
    /// or <see cref="DocFreshnessState.VeryStale"/>, the integer age in
    /// days of the most recent doc file. <c>null</c> for all other states.
    /// </returns>
    DocFreshnessResult Evaluate(string sessionId, DateTimeOffset sessionCreatedAt);
}

/// <summary>Result of <see cref="IDocFreshnessService.Evaluate"/>.</summary>
/// <param name="State">Traffic-light state.</param>
/// <param name="AgeDays">Whole-day age of the doc file; only set for stale states.</param>
public readonly record struct DocFreshnessResult(DocFreshnessState State, int? AgeDays);

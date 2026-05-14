using System;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels;

/// <summary>
/// V1.3 (#147) tests for the doc-freshness projections on
/// <see cref="SessionCardViewModel"/>: caption formatting and sort key.
/// </summary>
public sealed class DocFreshnessCaptionTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class StubDocFreshness : IDocFreshnessService
    {
        private readonly DocFreshnessResult _result;
        public StubDocFreshness(DocFreshnessResult result) => _result = result;
        public DocFreshnessResult Evaluate(string sessionId, DateTimeOffset sessionCreatedAt) => _result;
    }

    private static Session BuildSession() => new(
        Id: "abcdef1234567890",
        Cwd: @"C:\ws\repo",
        Repository: "owner/repo",
        Branch: "main",
        Summary: "Hello",
        HostType: "cli",
        CreatedAt: Now.AddDays(-2),
        UpdatedAt: Now,
        TurnCount: 1,
        Status: SessionStatus.Idle,
        CopilotVersion: CopilotVersion.Zero,
        Locks: Array.Empty<SessionLockInfo>());

    private static SessionCardViewModel CreateCard(DocFreshnessResult freshness) => new(
        BuildSession(), SessionType.Exploratory, new FixedTimeProvider(Now),
        modelCatalog: null, costCalculator: null, fileLauncher: null,
        lockCleanup: null, sessionLauncher: null, logger: null,
        openMergeWizard: null, issueLinks: null,
        runningSessions: null, windowActivator: null,
        displayNameStore: null, displayNameOverride: null,
        deletionService: null, confirmDelete: null,
        starStore: null, isStarred: false,
        onDeleted: null,
        docFreshness: new StubDocFreshness(freshness),
        readmeService: null);

    [Theory]
    [InlineData(DocFreshnessState.Fresh, null, "📄 ✓ fresh")]
    [InlineData(DocFreshnessState.Stale, 3, "📄 ⚠ stale 3d")]
    [InlineData(DocFreshnessState.VeryStale, 14, "📄 ⚠ stale 14d")]
    [InlineData(DocFreshnessState.Missing, null, "📄 ✗ missing")]
    [InlineData(DocFreshnessState.NotApplicable, null, "📄 — n/a")]
    public void DocFreshnessCaption_FormatsAccordingToState(
        DocFreshnessState state, int? ageDays, string expected)
    {
        var card = CreateCard(new DocFreshnessResult(state, ageDays));

        card.DocFreshness.Should().Be(state);
        card.DocFreshnessCaption.Should().Be(expected);
    }

    [Theory]
    [InlineData(DocFreshnessState.VeryStale, 0)]
    [InlineData(DocFreshnessState.Stale, 1)]
    [InlineData(DocFreshnessState.Missing, 2)]
    [InlineData(DocFreshnessState.Fresh, 3)]
    [InlineData(DocFreshnessState.NotApplicable, 4)]
    public void DocFreshnessSortKey_PutsStaleAndMissingFirst(DocFreshnessState state, int expectedKey)
    {
        var card = CreateCard(new DocFreshnessResult(state, AgeDays: null));

        card.DocFreshnessSortKey.Should().Be(expectedKey);
    }

    [Fact]
    public void DocFreshness_DefaultsToNotApplicable_WhenServiceIsNull()
    {
        var card = new SessionCardViewModel(BuildSession(), new FixedTimeProvider(Now));

        card.DocFreshness.Should().Be(DocFreshnessState.NotApplicable);
        card.DocFreshnessCaption.Should().Be("📄 — n/a");
    }
}

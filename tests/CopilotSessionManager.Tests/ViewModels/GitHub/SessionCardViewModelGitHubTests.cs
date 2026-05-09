using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Services;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels.GitHub;

public class SessionCardViewModelGitHubTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);

    private static Session BuildSession(SessionGitHubLinks? links = null) => new(
        Id: "abc",
        Cwd: null,
        Repository: "owner/repo",
        Branch: "main",
        Summary: "x",
        HostType: "cli",
        CreatedAt: Now.AddMinutes(-10),
        UpdatedAt: Now.AddMinutes(-1),
        TurnCount: 1,
        Status: SessionStatus.Idle,
        CopilotVersion: CopilotVersion.Zero,
        Locks: Array.Empty<SessionLockInfo>(),
        ModelInfo: null,
        GitHubLinks: links);

    private static SessionCardViewModel BuildCard(SessionGitHubLinks? links, IFileLauncher? launcher)
    {
        var tp = new FixedTimeProvider(Now);
        return new SessionCardViewModel(BuildSession(links), SessionType.Exploratory, tp,
            modelCatalog: null, costCalculator: null, fileLauncher: launcher);
    }

    [Fact]
    public void NoGitHubLinks_AllUrlsAreNull_AndPullRequestIsHidden()
    {
        var card = BuildCard(links: null, launcher: null);
        card.RepositoryUrl.Should().BeNull();
        card.BranchUrl.Should().BeNull();
        card.HasRepositoryUrl.Should().BeFalse();
        card.HasBranchUrl.Should().BeFalse();
        card.HasPullRequest.Should().BeFalse();
        card.PullRequestBadgeText.Should().BeEmpty();
    }

    [Fact]
    public void PopulatedLinks_ExposeUrls()
    {
        var links = new SessionGitHubLinks(
            RepositoryUrl: "https://github.com/owner/repo",
            BranchUrl: "https://github.com/owner/repo/tree/main",
            PullRequest: null);
        var card = BuildCard(links, launcher: null);
        card.RepositoryUrl.Should().Be("https://github.com/owner/repo");
        card.BranchUrl.Should().Be("https://github.com/owner/repo/tree/main");
        card.HasRepositoryUrl.Should().BeTrue();
        card.HasBranchUrl.Should().BeTrue();
        card.HasPullRequest.Should().BeFalse();
    }

    [Fact]
    public void SetPullRequest_PopulatesBadge_AndRaisesNotifications()
    {
        var card = BuildCard(links: null, launcher: null);
        var raised = new List<string>();
        card.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        var pr = new PullRequestInfo(42, "feat", PullRequestState.Open, "https://github.com/o/r/pull/42");
        card.SetPullRequest(pr);

        card.HasPullRequest.Should().BeTrue();
        card.PullRequestNumber.Should().Be(42);
        card.PullRequestUrl.Should().Be("https://github.com/o/r/pull/42");
        card.PullRequestBadgeText.Should().Be("#42");
        card.PullRequestStateLabel.Should().Be("Open");
        card.PullRequestTooltip.Should().Contain("PR #42").And.Contain("Open").And.Contain("feat");
        raised.Should().Contain(new[]
        {
            nameof(SessionCardViewModel.PullRequest),
            nameof(SessionCardViewModel.HasPullRequest),
            nameof(SessionCardViewModel.PullRequestNumber),
            nameof(SessionCardViewModel.PullRequestUrl),
            nameof(SessionCardViewModel.PullRequestBadgeText),
            nameof(SessionCardViewModel.PullRequestStateLabel),
            nameof(SessionCardViewModel.PullRequestTooltip),
            nameof(SessionCardViewModel.PullRequestStateBrush),
        });
    }

    [Fact]
    public void SetPullRequest_Null_ClearsBadge()
    {
        var card = BuildCard(links: null, launcher: null);
        card.SetPullRequest(new PullRequestInfo(1, "t", PullRequestState.Open, "u"));
        card.HasPullRequest.Should().BeTrue();
        card.SetPullRequest(null);
        card.HasPullRequest.Should().BeFalse();
        card.PullRequestUrl.Should().BeNull();
    }

    [Theory]
    [InlineData(PullRequestState.Open, 0xA6, 0xE3, 0xA1)]
    [InlineData(PullRequestState.Draft, 0x7F, 0x84, 0x9C)]
    [InlineData(PullRequestState.Merged, 0xCB, 0xA6, 0xF7)]
    [InlineData(PullRequestState.Closed, 0xF3, 0x8B, 0xA8)]
    public void PullRequestStateBrush_MatchesPalette(PullRequestState state, byte r, byte g, byte b)
    {
        var card = BuildCard(links: null, launcher: null);
        card.SetPullRequest(new PullRequestInfo(1, "t", state, "u"));
        var brush = (System.Windows.Media.SolidColorBrush)card.PullRequestStateBrush;
        brush.Color.R.Should().Be(r);
        brush.Color.G.Should().Be(g);
        brush.Color.B.Should().Be(b);
    }

    [Fact]
    public async Task OpenUrlCommand_CallsLauncher_WithProvidedUrl()
    {
        var launcher = new RecordingLauncher();
        var card = BuildCard(links: null, launcher: launcher);
        await ((CommunityToolkit.Mvvm.Input.AsyncRelayCommand<string?>)card.OpenUrlCommand)
            .ExecuteAsync("https://example.com/x");
        launcher.Calls.Should().ContainSingle().Which.Should().Be("https://example.com/x");
    }

    [Fact]
    public async Task OpenUrlCommand_NullUrl_DoesNothing()
    {
        var launcher = new RecordingLauncher();
        var card = BuildCard(links: null, launcher: launcher);
        await ((CommunityToolkit.Mvvm.Input.AsyncRelayCommand<string?>)card.OpenUrlCommand)
            .ExecuteAsync(null);
        launcher.Calls.Should().BeEmpty();
    }

    [Fact]
    public void OpenUrlCommand_CanExecute_RequiresUrlAndLauncher()
    {
        var withoutLauncher = BuildCard(links: null, launcher: null);
        withoutLauncher.OpenUrlCommand.CanExecute("https://x").Should().BeFalse();

        var withLauncher = BuildCard(links: null, launcher: new RecordingLauncher());
        withLauncher.OpenUrlCommand.CanExecute("https://x").Should().BeTrue();
        withLauncher.OpenUrlCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void UpdateFrom_ReplacingModel_ClearsPullRequestOverride()
    {
        var card = BuildCard(links: null, launcher: null);
        card.SetPullRequest(new PullRequestInfo(1, "t", PullRequestState.Open, "u"));
        card.HasPullRequest.Should().BeTrue();

        // Same id, fresh links — clear the PR override so a new lookup wins.
        var newLinks = new SessionGitHubLinks(
            RepositoryUrl: "https://github.com/owner/repo",
            BranchUrl: "https://github.com/owner/repo/tree/main",
            PullRequest: null);
        card.UpdateFrom(BuildSession(newLinks));
        card.HasPullRequest.Should().BeFalse();
        card.RepositoryUrl.Should().Be("https://github.com/owner/repo");
    }

    private sealed class RecordingLauncher : IFileLauncher
    {
        public List<string> Calls { get; } = new();
        public Task OpenAsync(string path, CancellationToken cancellationToken = default)
        {
            Calls.Add(path);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}

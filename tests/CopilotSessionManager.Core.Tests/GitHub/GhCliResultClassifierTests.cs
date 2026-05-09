using CopilotSessionManager.Core.GitHub;
using CopilotSessionManager.Core.Onboarding;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.GitHub;

public class GhCliResultClassifierTests
{
    [Fact]
    public void ZeroExit_ClassifiedAsAvailable()
    {
        var (state, message) = GhCliResultClassifier.Classify(new ProcessRunResult(0, "[]", ""));
        state.Should().Be(GitHubAvailability.Available);
        message.Should().BeNull();
    }

    [Theory]
    [InlineData("could not resolve host github.com")]
    [InlineData("error: dial tcp: lookup api.github.com: no such host")]
    [InlineData("connect: network is unreachable")]
    [InlineData("write tcp 1.2.3.4: connection reset by peer")]
    [InlineData("Get \"https://api.github.com\": net/http: TLS handshake timeout")]
    [InlineData("dial tcp 140.82.112.5:443: i/o timeout")]
    [InlineData("dial tcp 140.82.112.5:443: connect: connection refused")]
    [InlineData("dial tcp: lookup api.github.com on 8.8.8.8:53: server misbehaving")]
    public void NetworkErrorsInStderr_ClassifiedAsOffline(string stderr)
    {
        var (state, message) = GhCliResultClassifier.Classify(new ProcessRunResult(1, "", stderr));
        state.Should().Be(GitHubAvailability.Offline);
        message.Should().NotBeNullOrWhiteSpace();
        message.Should().Contain("offline");
    }

    [Theory]
    [InlineData("error: gh auth status returned: not authenticated")]
    [InlineData("Authentication required. Please run: gh auth login")]
    [InlineData("HTTP 401: Bad credentials")]
    [InlineData("This endpoint requires authentication.")]
    [InlineData("You are not logged into any GitHub hosts.")]
    public void AuthErrorsInStderr_ClassifiedAsUnauthenticated(string stderr)
    {
        var (state, message) = GhCliResultClassifier.Classify(new ProcessRunResult(1, "", stderr));
        state.Should().Be(GitHubAvailability.Unauthenticated);
        message.Should().NotBeNullOrWhiteSpace();
        message.Should().Contain("gh auth login");
    }

    [Fact]
    public void NetworkErrorInStdout_AlsoClassifiedAsOffline()
    {
        var (state, _) = GhCliResultClassifier.Classify(
            new ProcessRunResult(1, "could not resolve host", ""));
        state.Should().Be(GitHubAvailability.Offline);
    }

    [Fact]
    public void UnknownNonZeroFailure_ClassifiedAsAvailable_SignalingDontReport()
    {
        // Classifier returns Available + null when it can't tell — caller
        // (GhCliGitHubClient) uses that signal to skip Report() so we don't
        // wrongly mark GitHub as recovered after a random API error.
        var (state, message) = GhCliResultClassifier.Classify(
            new ProcessRunResult(1, "no PR found", "unrelated error message"));
        state.Should().Be(GitHubAvailability.Available);
        message.Should().BeNull();
    }

    [Fact]
    public void ClassificationIsCaseInsensitive()
    {
        var (state, _) = GhCliResultClassifier.Classify(
            new ProcessRunResult(1, "", "ERROR: COULD NOT RESOLVE HOST github.com"));
        state.Should().Be(GitHubAvailability.Offline);
    }

    [Fact]
    public void NullResult_Throws()
    {
        var act = () => GhCliResultClassifier.Classify(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ContainsAny_FindsMatch()
    {
        GhCliResultClassifier.ContainsAny("abc def", new[] { "xyz", "def" }).Should().BeTrue();
        GhCliResultClassifier.ContainsAny("abc def", new[] { "xyz", "qqq" }).Should().BeFalse();
    }
}

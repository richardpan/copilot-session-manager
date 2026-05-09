using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CopilotSessionManager.Core.GitHub.Storage;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.GitHub.Storage;

public class JsonSessionGitHubLinksStoreTests : IDisposable
{
    private readonly string _root;

    public JsonSessionGitHubLinksStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csm-gh-overrides-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private JsonSessionGitHubLinksStore CreateSut() =>
        new(new FakeFolderReader(_root), NullLogger<JsonSessionGitHubLinksStore>.Instance);

    [Fact]
    public void GetOverridesPath_PutsFileInsideSessionFolder()
    {
        var path = CreateSut().GetOverridesPath("abc");
        path.Should().Be(Path.Combine(_root, "abc", JsonSessionGitHubLinksStore.FileName));
    }

    [Fact]
    public async Task GetAsync_FileMissing_ReturnsNull()
    {
        var sut = CreateSut();
        (await sut.GetAsync("nope")).Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_ThenGetAsync_RoundTripsAllFields()
    {
        var sut = CreateSut();
        var input = new SessionGitHubLinkOverrides("user/repo", "https://github.com/user/repo/tree/feat", 42);

        await sut.SetAsync("s1", input);
        var loaded = await sut.GetAsync("s1");

        loaded.Should().NotBeNull();
        loaded!.RepositoryOverride.Should().Be("user/repo");
        loaded.BranchOverride.Should().Be("https://github.com/user/repo/tree/feat");
        loaded.PullRequestNumberOverride.Should().Be(42);
    }

    [Fact]
    public async Task SetAsync_PartialOverride_RoundTripsNullsAsNull()
    {
        var sut = CreateSut();
        var input = new SessionGitHubLinkOverrides(RepositoryOverride: null, BranchOverride: null, PullRequestNumberOverride: 7);

        await sut.SetAsync("s1", input);
        var loaded = await sut.GetAsync("s1");

        loaded.Should().NotBeNull();
        loaded!.RepositoryOverride.Should().BeNull();
        loaded.BranchOverride.Should().BeNull();
        loaded.PullRequestNumberOverride.Should().Be(7);
    }

    [Fact]
    public async Task SetAsync_WithEmptyOverrides_RemovesFile()
    {
        var sut = CreateSut();
        await sut.SetAsync("s1", new SessionGitHubLinkOverrides("foo/bar", null, null));
        File.Exists(sut.GetOverridesPath("s1")).Should().BeTrue();

        await sut.SetAsync("s1", SessionGitHubLinkOverrides.Empty);

        File.Exists(sut.GetOverridesPath("s1")).Should().BeFalse();
        (await sut.GetAsync("s1")).Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_PersistsAcrossInstances()
    {
        var first = CreateSut();
        await first.SetAsync("s1", new SessionGitHubLinkOverrides("o/r", null, 9));

        var second = CreateSut();
        var loaded = await second.GetAsync("s1");

        loaded.Should().NotBeNull();
        loaded!.RepositoryOverride.Should().Be("o/r");
        loaded.PullRequestNumberOverride.Should().Be(9);
    }

    [Fact]
    public async Task GetAsync_MalformedJson_ReturnsNullAndBacksUpFile()
    {
        var sut = CreateSut();
        var path = sut.GetOverridesPath("s1");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ this is not valid json");

        var result = await sut.GetAsync("s1");

        result.Should().BeNull();

        // Original file moved aside as .bak.<unix-seconds>; main file no longer present.
        File.Exists(path).Should().BeFalse();
        var backups = Directory
            .GetFiles(Path.GetDirectoryName(path)!, JsonSessionGitHubLinksStore.FileName + ".bak.*");
        backups.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetAsync_AllFieldsNullInDocument_ReturnsNull()
    {
        var sut = CreateSut();
        var path = sut.GetOverridesPath("s1");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ \"version\": 1 }");

        (await sut.GetAsync("s1")).Should().BeNull();
    }

    [Fact]
    public async Task ClearAsync_RemovesFile_AndIsIdempotentWhenMissing()
    {
        var sut = CreateSut();
        await sut.SetAsync("s1", new SessionGitHubLinkOverrides("o/r", null, null));
        File.Exists(sut.GetOverridesPath("s1")).Should().BeTrue();

        await sut.ClearAsync("s1");
        File.Exists(sut.GetOverridesPath("s1")).Should().BeFalse();

        // Second call must not throw even though the file is gone.
        await sut.ClearAsync("s1");
        (await sut.GetAsync("s1")).Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_OverwritesPreviousValue()
    {
        var sut = CreateSut();
        await sut.SetAsync("s1", new SessionGitHubLinkOverrides("a/b", "x", 1));
        await sut.SetAsync("s1", new SessionGitHubLinkOverrides("c/d", "y", 2));

        var loaded = await sut.GetAsync("s1");
        loaded.Should().NotBeNull();
        loaded!.RepositoryOverride.Should().Be("c/d");
        loaded.BranchOverride.Should().Be("y");
        loaded.PullRequestNumberOverride.Should().Be(2);
    }

    [Fact]
    public async Task SetAsync_ConcurrentWrites_DoNotCorruptFile()
    {
        var sut = CreateSut();

        var tasks = Enumerable.Range(0, 16).Select(i =>
            sut.SetAsync(
                "s1",
                new SessionGitHubLinkOverrides($"owner/repo-{i}", null, i))).ToArray();
        await Task.WhenAll(tasks);

        var loaded = await sut.GetAsync("s1");
        loaded.Should().NotBeNull();

        // Whichever winner came last, both fields must agree (no torn writes).
        loaded!.RepositoryOverride.Should().StartWith("owner/repo-");
        var winnerIndex = int.Parse(loaded.RepositoryOverride!["owner/repo-".Length..]);
        loaded.PullRequestNumberOverride.Should().Be(winnerIndex);
    }

    [Fact]
    public async Task SetAsync_DifferentSessions_AreIsolated()
    {
        var sut = CreateSut();
        await sut.SetAsync("s1", new SessionGitHubLinkOverrides("o/r1", null, 1));
        await sut.SetAsync("s2", new SessionGitHubLinkOverrides("o/r2", null, 2));

        (await sut.GetAsync("s1"))!.RepositoryOverride.Should().Be("o/r1");
        (await sut.GetAsync("s2"))!.RepositoryOverride.Should().Be("o/r2");
    }

    [Fact]
    public async Task SetAsync_BlankRepositoryString_RoundTripsAsNull()
    {
        // Leading/trailing whitespace in the repository field should normalize
        // to "no repository override" on read so the discovery output wins.
        var sut = CreateSut();
        await sut.SetAsync("s1", new SessionGitHubLinkOverrides("   ", null, 5));

        var loaded = await sut.GetAsync("s1");
        loaded.Should().NotBeNull();
        loaded!.RepositoryOverride.Should().BeNull();
        loaded.PullRequestNumberOverride.Should().Be(5);
    }

    [Fact]
    public async Task GetAsync_NullSessionId_Throws()
    {
        var sut = CreateSut();
        await FluentActions.Invoking(() => sut.GetAsync(null!))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SetAsync_NullOverrides_Throws()
    {
        var sut = CreateSut();
        await FluentActions.Invoking(() => sut.SetAsync("s1", null!))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void OverridesRecord_HasAnyOverride_TracksFields()
    {
        SessionGitHubLinkOverrides.Empty.HasAnyOverride.Should().BeFalse();
        new SessionGitHubLinkOverrides("o/r", null, null).HasAnyOverride.Should().BeTrue();
        new SessionGitHubLinkOverrides(null, "b", null).HasAnyOverride.Should().BeTrue();
        new SessionGitHubLinkOverrides(null, null, 42).HasAnyOverride.Should().BeTrue();
    }

    private sealed class FakeFolderReader : ISessionFolderReader
    {
        private readonly string _root;
        public FakeFolderReader(string root) => _root = root;

        public string GetSessionFolderPath(string sessionId) => Path.Combine(_root, sessionId);

        public Task<System.Collections.Generic.IReadOnlyList<Core.Models.SessionCheckpointSummary>>
            GetCheckpointsAsync(string sessionId, System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult<System.Collections.Generic.IReadOnlyList<Core.Models.SessionCheckpointSummary>>(
                Array.Empty<Core.Models.SessionCheckpointSummary>());
    }
}

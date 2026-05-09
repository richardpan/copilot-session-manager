using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CopilotSessionManager.Core.GitHub.Issues;
using CopilotSessionManager.Core.GitHub.Storage;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.GitHub.Storage;

/// <summary>
/// Issue-link round-trip + dedupe coverage for
/// <see cref="JsonSessionGitHubLinksStore"/>. Lives in a sibling test class
/// so the v1 baseline tests stay focused on repo/branch/PR overrides.
/// </summary>
public class JsonSessionGitHubLinksStoreIssuesTests : IDisposable
{
    private readonly string _root;

    public JsonSessionGitHubLinksStoreIssuesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csm-gh-issues-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private JsonSessionGitHubLinksStore CreateSut() =>
        new(new FakeFolderReader(_root), NullLogger<JsonSessionGitHubLinksStore>.Instance);

    [Fact]
    public async Task SetAsync_RoundTripsIssueRefs()
    {
        var sut = CreateSut();
        var input = new SessionGitHubLinkOverrides("o/r", null, null)
        {
            IssueRefs = new[] { "o/r#1", "other/repo#42" },
        };

        await sut.SetAsync("s1", input);
        var loaded = await sut.GetAsync("s1");

        loaded.Should().NotBeNull();
        loaded!.IssueRefs.Should().Equal("o/r#1", "other/repo#42");
        loaded.RepositoryOverride.Should().Be("o/r");
    }

    [Fact]
    public async Task V1_document_without_issueRefs_is_read_with_empty_list()
    {
        var sut = CreateSut();
        var path = sut.GetOverridesPath("s1");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """
        {
          "version": 1,
          "repository": "owner/repo",
          "pullRequestNumber": 42
        }
        """);

        var loaded = await sut.GetAsync("s1");

        loaded.Should().NotBeNull();
        loaded!.RepositoryOverride.Should().Be("owner/repo");
        loaded.PullRequestNumberOverride.Should().Be(42);
        loaded.IssueRefs.Should().BeEmpty();
    }

    [Fact]
    public async Task SetAsync_PersistsIssueRefsBumpsToVersion2()
    {
        var sut = CreateSut();
        var input = new SessionGitHubLinkOverrides(null, null, null)
        {
            IssueRefs = new[] { "o/r#9" },
        };

        await sut.SetAsync("s1", input);

        var path = sut.GetOverridesPath("s1");
        var json = await File.ReadAllTextAsync(path);
        json.Should().Contain("\"version\": 2");
        json.Should().Contain("\"issueRefs\"");
        json.Should().Contain("o/r#9");
    }

    [Fact]
    public async Task SetAsync_WithOnlyIssueRefs_PersistsFile()
    {
        var sut = CreateSut();
        var input = new SessionGitHubLinkOverrides(null, null, null)
        {
            IssueRefs = new[] { "o/r#1" },
        };

        await sut.SetAsync("s1", input);

        File.Exists(sut.GetOverridesPath("s1")).Should().BeTrue();
        var loaded = await sut.GetAsync("s1");
        loaded.Should().NotBeNull();
        loaded!.IssueRefs.Should().ContainSingle().Which.Should().Be("o/r#1");
    }

    [Fact]
    public async Task AddIssueRefAsync_AppendsToExistingList()
    {
        var sut = CreateSut();
        await sut.AddIssueRefAsync("s1", new IssueRef("o/r", 1));
        await sut.AddIssueRefAsync("s1", new IssueRef("o/r", 2));

        var loaded = await sut.GetAsync("s1");
        loaded.Should().NotBeNull();
        loaded!.IssueRefs.Should().Equal("o/r#1", "o/r#2");
    }

    [Fact]
    public async Task AddIssueRefAsync_DeduplicatesCaseInsensitive()
    {
        var sut = CreateSut();
        await sut.AddIssueRefAsync("s1", new IssueRef("Owner/Repo", 1));
        await sut.AddIssueRefAsync("s1", new IssueRef("owner/repo", 1));
        await sut.AddIssueRefAsync("s1", new IssueRef("OWNER/REPO", 1));

        var loaded = await sut.GetAsync("s1");
        loaded.Should().NotBeNull();
        loaded!.IssueRefs.Should().ContainSingle().Which.Should().Be("owner/repo#1");
    }

    [Fact]
    public async Task AddIssueRefAsync_PreservesOtherOverrides()
    {
        var sut = CreateSut();
        await sut.SetAsync("s1", new SessionGitHubLinkOverrides("o/r", "branch", 7));
        await sut.AddIssueRefAsync("s1", new IssueRef("o/r", 9));

        var loaded = await sut.GetAsync("s1");
        loaded.Should().NotBeNull();
        loaded!.RepositoryOverride.Should().Be("o/r");
        loaded.BranchOverride.Should().Be("branch");
        loaded.PullRequestNumberOverride.Should().Be(7);
        loaded.IssueRefs.Should().Equal("o/r#9");
    }

    [Fact]
    public async Task RemoveIssueRefAsync_DropsEntry()
    {
        var sut = CreateSut();
        await sut.AddIssueRefAsync("s1", new IssueRef("o/r", 1));
        await sut.AddIssueRefAsync("s1", new IssueRef("o/r", 2));
        await sut.RemoveIssueRefAsync("s1", new IssueRef("o/r", 1));

        var loaded = await sut.GetAsync("s1");
        loaded.Should().NotBeNull();
        loaded!.IssueRefs.Should().ContainSingle().Which.Should().Be("o/r#2");
    }

    [Fact]
    public async Task RemoveIssueRefAsync_NoOpsOnMissing()
    {
        var sut = CreateSut();
        await sut.RemoveIssueRefAsync("s1", new IssueRef("o/r", 1));
        (await sut.GetAsync("s1")).Should().BeNull();

        await sut.AddIssueRefAsync("s1", new IssueRef("o/r", 1));
        await sut.RemoveIssueRefAsync("s1", new IssueRef("o/r", 999));

        var loaded = await sut.GetAsync("s1");
        loaded.Should().NotBeNull();
        loaded!.IssueRefs.Should().Equal("o/r#1");
    }

    [Fact]
    public async Task RemoveIssueRefAsync_LastIssueRefAndNoOtherOverrides_DeletesFile()
    {
        var sut = CreateSut();
        await sut.AddIssueRefAsync("s1", new IssueRef("o/r", 1));
        File.Exists(sut.GetOverridesPath("s1")).Should().BeTrue();

        await sut.RemoveIssueRefAsync("s1", new IssueRef("o/r", 1));

        File.Exists(sut.GetOverridesPath("s1")).Should().BeFalse();
        (await sut.GetAsync("s1")).Should().BeNull();
    }

    [Fact]
    public async Task RemoveIssueRefAsync_LastIssueRefWithOtherOverrides_KeepsFile()
    {
        var sut = CreateSut();
        await sut.SetAsync("s1", new SessionGitHubLinkOverrides("o/r", null, null)
        {
            IssueRefs = new[] { "o/r#1" },
        });

        await sut.RemoveIssueRefAsync("s1", new IssueRef("o/r", 1));

        File.Exists(sut.GetOverridesPath("s1")).Should().BeTrue();
        var loaded = await sut.GetAsync("s1");
        loaded.Should().NotBeNull();
        loaded!.IssueRefs.Should().BeEmpty();
        loaded.RepositoryOverride.Should().Be("o/r");
    }

    [Fact]
    public async Task ReadingDuplicateIssueRefsFromFile_NormalisesToUniqueList()
    {
        var sut = CreateSut();
        var path = sut.GetOverridesPath("s1");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """
        {
          "version": 2,
          "issueRefs": ["o/r#1", "o/r#1", "  ", null, "o/r#2"]
        }
        """);

        var loaded = await sut.GetAsync("s1");
        loaded.Should().NotBeNull();
        loaded!.IssueRefs.Should().Equal("o/r#1", "o/r#2");
    }

    [Fact]
    public async Task EmptyOverridesAfterIssueRefRemoval_ReturnsNull()
    {
        var sut = CreateSut();
        await sut.AddIssueRefAsync("s1", new IssueRef("o/r", 1));
        await sut.RemoveIssueRefAsync("s1", new IssueRef("o/r", 1));

        (await sut.GetAsync("s1")).Should().BeNull();
    }

    [Fact]
    public void Overrides_HasAnyOverride_TracksIssueRefs()
    {
        var withRefs = new SessionGitHubLinkOverrides(null, null, null)
        {
            IssueRefs = new[] { "o/r#1" },
        };
        withRefs.HasAnyOverride.Should().BeTrue();

        var empty = new SessionGitHubLinkOverrides(null, null, null);
        empty.HasAnyOverride.Should().BeFalse();
        empty.IssueRefs.Should().BeEmpty();
    }

    [Fact]
    public async Task NullArguments_Throw()
    {
        var sut = CreateSut();
        await FluentActions.Invoking(() => sut.AddIssueRefAsync("s1", null!))
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Invoking(() => sut.RemoveIssueRefAsync("s1", null!))
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Invoking(() => sut.AddIssueRefAsync(" ", new IssueRef("o/r", 1)))
            .Should().ThrowAsync<ArgumentException>();
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

using System;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.GitHub.Issues;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.GitHub.Issues;

public class ReadmeIssueRefProviderTests
{
    private const string SessionId = "0123456789abcdef0123456789abcdef01234567";
    private const string DefaultRepo = "octo/widgets";

    [Fact]
    public async Task GetParsedRefsAsync_HappyPath_ReturnsScannedRefs()
    {
        var store = new FakeReadmeStore { Content = "Closes #42 and acme/tools#7." };
        var sut = new ReadmeIssueRefProvider(store, NullLogger<ReadmeIssueRefProvider>.Instance);

        var refs = await sut.GetParsedRefsAsync(SessionId, DefaultRepo);

        refs.Should().HaveCount(2);
        refs[0].ToString().Should().Be("octo/widgets#42");
        refs[1].ToString().Should().Be("acme/tools#7");
    }

    [Fact]
    public async Task GetParsedRefsAsync_EmptyReadme_ReturnsEmpty()
    {
        var store = new FakeReadmeStore { Content = "" };
        var sut = new ReadmeIssueRefProvider(store, NullLogger<ReadmeIssueRefProvider>.Instance);

        var refs = await sut.GetParsedRefsAsync(SessionId, DefaultRepo);

        refs.Should().BeEmpty();
    }

    [Fact]
    public async Task GetParsedRefsAsync_MissingReadme_ReturnsEmpty()
    {
        var store = new FakeReadmeStore { Content = null };
        var sut = new ReadmeIssueRefProvider(store, NullLogger<ReadmeIssueRefProvider>.Instance);

        var refs = await sut.GetParsedRefsAsync(SessionId, DefaultRepo);

        refs.Should().BeEmpty();
    }

    [Fact]
    public async Task GetParsedRefsAsync_StoreThrows_ReturnsEmpty()
    {
        var store = new FakeReadmeStore { ThrowOnRead = new InvalidOperationException("boom") };
        var sut = new ReadmeIssueRefProvider(store, NullLogger<ReadmeIssueRefProvider>.Instance);

        var refs = await sut.GetParsedRefsAsync(SessionId, DefaultRepo);

        refs.Should().BeEmpty();
    }

    [Fact]
    public async Task GetParsedRefsAsync_StoreThrowsOperationCanceled_PropagatesCancellation()
    {
        var store = new FakeReadmeStore
        {
            ThrowOnRead = new OperationCanceledException(),
        };
        var sut = new ReadmeIssueRefProvider(store, NullLogger<ReadmeIssueRefProvider>.Instance);

        var act = async () => await sut.GetParsedRefsAsync(SessionId, DefaultRepo);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetParsedRefsAsync_NullSessionId_Throws()
    {
        var store = new FakeReadmeStore();
        var sut = new ReadmeIssueRefProvider(store);

        var act = async () => await sut.GetParsedRefsAsync(" ", DefaultRepo);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Constructor_NullStore_Throws()
    {
        Action act = () => new ReadmeIssueRefProvider(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ReadmeChanged_ForwardsStoreEvent()
    {
        var store = new FakeReadmeStore();
        using var sut = new ReadmeIssueRefProvider(store);

        ReadmeIssueRefsChangedEventArgs? received = null;
        sut.ReadmeChanged += (_, e) => received = e;

        store.RaiseChanged(SessionId);

        received.Should().NotBeNull();
        received!.SessionId.Should().Be(SessionId);
    }

    [Fact]
    public void Dispose_UnsubscribesFromStore()
    {
        var store = new FakeReadmeStore();
        var sut = new ReadmeIssueRefProvider(store);
        var raised = 0;
        sut.ReadmeChanged += (_, _) => raised++;

        sut.Dispose();
        store.RaiseChanged(SessionId);

        raised.Should().Be(0);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var store = new FakeReadmeStore();
        var sut = new ReadmeIssueRefProvider(store);

        sut.Dispose();
        var act = sut.Dispose;
        act.Should().NotThrow();
    }

    [Fact]
    public async Task GetParsedRefsAsync_BlankReadme_ReturnsEmpty()
    {
        var store = new FakeReadmeStore { Content = "   \r\n\t  " };
        var sut = new ReadmeIssueRefProvider(store);

        var refs = await sut.GetParsedRefsAsync(SessionId, DefaultRepo);

        refs.Should().BeEmpty();
    }

    private sealed class FakeReadmeStore : ISessionReadmeStore
    {
        public string? Content { get; set; }
        public Exception? ThrowOnRead { get; set; }

        public event EventHandler<SessionReadmeChangedEventArgs>? ReadmeChanged;

        public string GetReadmePath(string sessionId) => $"/fake/{sessionId}/SESSION-README.md";

        public bool Exists(string sessionId) => Content is not null;

        public Task<string?> ReadAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            if (ThrowOnRead is not null)
            {
                throw ThrowOnRead;
            }
            return Task.FromResult(Content);
        }

        public Task<string> WriteAsync(string sessionId, string freshlyRendered, CancellationToken cancellationToken = default)
        {
            Content = freshlyRendered;
            ReadmeChanged?.Invoke(this, new SessionReadmeChangedEventArgs(sessionId, GetReadmePath(sessionId)));
            return Task.FromResult(freshlyRendered);
        }

        public void RaiseChanged(string sessionId)
        {
            ReadmeChanged?.Invoke(this, new SessionReadmeChangedEventArgs(sessionId, GetReadmePath(sessionId)));
        }
    }
}

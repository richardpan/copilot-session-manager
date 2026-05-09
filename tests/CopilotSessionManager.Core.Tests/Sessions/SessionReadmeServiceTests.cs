using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Sessions;

public class SessionReadmeServiceTests
{
    private static Session Build(string id = "abc") => new(
        Id: id,
        Cwd: @"C:\ws",
        Repository: "owner/repo",
        Branch: "main",
        Summary: "Do stuff",
        HostType: "vscode",
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow,
        TurnCount: 1,
        Status: SessionStatus.Idle,
        CopilotVersion: new CopilotVersion(1, 0, 0),
        Locks: Array.Empty<SessionLockInfo>());

    private static (SessionReadmeService svc, FakeStore store, FakeRenderer renderer, FakeFolders folders) CreateSut()
    {
        var renderer = new FakeRenderer();
        var folders = new FakeFolders();
        var store = new FakeStore();
        var svc = new SessionReadmeService(renderer, store, folders, NullLogger<SessionReadmeService>.Instance);
        return (svc, store, renderer, folders);
    }

    [Fact]
    public void GetReadmePath_DelegatesToStore()
    {
        var (svc, store, _, _) = CreateSut();
        svc.GetReadmePath("abc").Should().Be(store.GetReadmePath("abc"));
    }

    [Fact]
    public async Task EnsureAsync_PassesCheckpointsAndLabel_ToRenderer()
    {
        var (svc, _, renderer, folders) = CreateSut();
        folders.Checkpoints["abc"] = new[] { new SessionCheckpointSummary(1, "Plan", "/p.md") };

        await svc.EnsureAsync(Build("abc"), SessionType.Refactor);

        renderer.LastContext.Should().NotBeNull();
        renderer.LastContext!.Label.Should().Be(SessionType.Refactor);
        renderer.LastContext.Checkpoints.Should().HaveCount(1);
        renderer.LastContext.Checkpoints[0].Title.Should().Be("Plan");
    }

    [Fact]
    public async Task EnsureAsync_WritesRenderedContent_AndReturnsResult()
    {
        var (svc, store, renderer, _) = CreateSut();
        renderer.Output = "# rendered\n";

        var result = await svc.EnsureAsync(Build("abc"), SessionType.Bug);

        store.LastWritten.Should().Be("# rendered\n");
        result.Should().Be("# rendered\n");
    }

    [Fact]
    public async Task EnsureAsync_PropagatesStoreFailures()
    {
        var (svc, store, _, _) = CreateSut();
        store.ThrowOnWrite = true;
        var act = async () => await svc.EnsureAsync(Build(), SessionType.Bug);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private sealed class FakeRenderer : ISessionReadmeRenderer
    {
        public SessionReadmeContext? LastContext { get; private set; }
        public string Output { get; set; } = "# default\n";
        public string Render(SessionReadmeContext context)
        {
            LastContext = context;
            return Output;
        }
    }

    private sealed class FakeFolders : ISessionFolderReader
    {
        public Dictionary<string, IReadOnlyList<SessionCheckpointSummary>> Checkpoints { get; } = new();
        public string GetSessionFolderPath(string sessionId) => $"/sessions/{sessionId}";
        public Task<IReadOnlyList<SessionCheckpointSummary>> GetCheckpointsAsync(
            string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Checkpoints.TryGetValue(sessionId, out var cs)
                ? cs
                : (IReadOnlyList<SessionCheckpointSummary>)Array.Empty<SessionCheckpointSummary>());
    }

    private sealed class FakeStore : ISessionReadmeStore
    {
        public string? LastWritten { get; private set; }
        public bool ThrowOnWrite { get; set; }
#pragma warning disable CS0067
        public event EventHandler<SessionReadmeChangedEventArgs>? ReadmeChanged;
#pragma warning restore CS0067
        public string GetReadmePath(string sessionId) => $"/sessions/{sessionId}/SESSION-README.md";
        public bool Exists(string sessionId) => false;
        public Task<string?> ReadAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
        public Task<string> WriteAsync(string sessionId, string freshlyRendered,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnWrite)
            {
                throw new InvalidOperationException("disk full");
            }
            LastWritten = freshlyRendered;
            return Task.FromResult(freshlyRendered);
        }
    }
}

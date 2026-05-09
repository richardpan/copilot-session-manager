using System;
using System.IO;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Sessions;

public class SessionLockCleanupTests : IDisposable
{
    private readonly string _root;
    private readonly TempPaths _paths;

    public SessionLockCleanupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csm-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new TempPaths(_root);
    }

    public void Dispose()
    {
        try
        { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private SessionLockCleanup CreateSut(IFakeChecker? checker = null)
    {
        var monitor = new SessionLockMonitor(_paths,
            new FakeProcessChecker(checker ?? new AllDeadChecker()),
            NullLogger<SessionLockMonitor>.Instance);
        return new SessionLockCleanup(_paths, monitor, NullLogger<SessionLockCleanup>.Instance);
    }

    private string SeedSession(string id, params (int pid, bool isAliveStub)[] locks)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        foreach (var (pid, _) in locks)
        {
            File.WriteAllText(Path.Combine(dir, $"inuse.{pid}.lock"), "");
        }
        return dir;
    }

    [Fact]
    public async Task CleanupAsync_RemovesOnlyDeadLocks()
    {
        var dir = SeedSession("s1", (100, false), (200, true), (300, false));
        var checker = new SelectiveChecker(alive: new[] { 200 });
        var sut = CreateSut(checker);

        var removed = await sut.CleanupAsync("s1");

        removed.Should().Be(2);
        File.Exists(Path.Combine(dir, "inuse.100.lock")).Should().BeFalse();
        File.Exists(Path.Combine(dir, "inuse.200.lock")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "inuse.300.lock")).Should().BeFalse();
    }

    [Fact]
    public async Task CleanupAsync_NoLocks_ReturnsZero()
    {
        SeedSession("s1");
        var sut = CreateSut();
        (await sut.CleanupAsync("s1")).Should().Be(0);
    }

    [Fact]
    public async Task CleanupAsync_LiveLockStaysUntouched()
    {
        var dir = SeedSession("s1", (200, true));
        var sut = CreateSut(new SelectiveChecker(alive: new[] { 200 }));

        var removed = await sut.CleanupAsync("s1");

        removed.Should().Be(0);
        File.Exists(Path.Combine(dir, "inuse.200.lock")).Should().BeTrue();
    }

    [Fact]
    public async Task CleanupAllAsync_AggregatesAcrossSessions()
    {
        SeedSession("s1", (1, false), (2, false));
        SeedSession("s2", (3, true));               // live, untouched
        SeedSession("s3", (4, false));
        SeedSession("s4");                            // empty
        var checker = new SelectiveChecker(alive: new[] { 3 });
        var sut = CreateSut(checker);

        var result = await sut.CleanupAllAsync();

        result.LocksRemoved.Should().Be(3);
        result.SessionsAffected.Should().Be(2, "s1 and s3 had stale locks; s2 was live; s4 was empty");
    }

    [Fact]
    public async Task CleanupAllAsync_MissingRoot_ReturnsEmpty()
    {
        var missing = new TempPaths(Path.Combine(_root, "does-not-exist"));
        var monitor = new SessionLockMonitor(missing,
            new FakeProcessChecker(new AllDeadChecker()),
            NullLogger<SessionLockMonitor>.Instance);
        var sut = new SessionLockCleanup(missing, monitor, NullLogger<SessionLockCleanup>.Instance);

        var result = await sut.CleanupAllAsync();
        result.Should().BeSameAs(SessionLockCleanupResult.Empty);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task CleanupAsync_RejectsBlankSessionId(string? id)
    {
        var sut = CreateSut();
        await FluentActions.Invoking(() => sut.CleanupAsync(id!))
            .Should().ThrowAsync<ArgumentException>();
    }

    private interface IFakeChecker
    {
        bool IsAlive(int pid);
    }

    private sealed class AllDeadChecker : IFakeChecker
    {
        public bool IsAlive(int pid) => false;
    }

    private sealed class SelectiveChecker : IFakeChecker
    {
        private readonly HashSet<int> _alive;
        public SelectiveChecker(IEnumerable<int> alive) => _alive = new HashSet<int>(alive);
        public bool IsAlive(int pid) => _alive.Contains(pid);
    }

    private sealed class FakeProcessChecker : IProcessChecker
    {
        private readonly IFakeChecker _inner;
        public FakeProcessChecker(IFakeChecker inner) => _inner = inner;
        public bool IsAlive(int processId) => _inner.IsAlive(processId);
    }

    private sealed class TempPaths : ICopilotPaths
    {
        public TempPaths(string root) => SessionStateDirectory = root;
        public string SessionStoreDatabasePath => Path.Combine(SessionStateDirectory, "session-store.db");
        public string SessionStateDirectory { get; }
    }
}

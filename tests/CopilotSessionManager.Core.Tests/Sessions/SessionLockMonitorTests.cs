using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotSessionManager.Core.Tests.Sessions;

public class SessionLockMonitorTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _stateDir;
    private readonly TestPaths _paths;

    public SessionLockMonitorTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "csm-lock-" + Guid.NewGuid().ToString("N"));
        _stateDir = Path.Combine(_tempRoot, "session-state");
        Directory.CreateDirectory(_stateDir);
        _paths = new TestPaths(Path.Combine(_tempRoot, "session-store.db"), _stateDir);
    }

    [Fact]
    public void GetLocks_returns_empty_when_session_directory_missing()
    {
        var monitor = new SessionLockMonitor(_paths, new FakeProcessChecker(_ => true), NullLogger<SessionLockMonitor>.Instance);

        var locks = monitor.GetLocks("missing-session");

        locks.Should().BeEmpty();
    }

    [Fact]
    public void GetLocks_returns_empty_when_no_lock_files_exist()
    {
        var id = Guid.NewGuid().ToString();
        Directory.CreateDirectory(Path.Combine(_stateDir, id));

        var monitor = new SessionLockMonitor(_paths, new FakeProcessChecker(_ => true), NullLogger<SessionLockMonitor>.Instance);

        monitor.GetLocks(id).Should().BeEmpty();
    }

    [Fact]
    public void GetLocks_marks_lock_alive_when_process_checker_returns_true()
    {
        var id = Guid.NewGuid().ToString();
        var dir = Path.Combine(_stateDir, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "inuse.4242.lock"), "4242\n");

        var monitor = new SessionLockMonitor(_paths, new FakeProcessChecker(pid => pid == 4242), NullLogger<SessionLockMonitor>.Instance);

        var locks = monitor.GetLocks(id);

        locks.Should().ContainSingle();
        locks[0].ProcessId.Should().Be(4242);
        locks[0].IsAlive.Should().BeTrue();
        locks[0].LockFilePath.Should().EndWith("inuse.4242.lock");
    }

    [Fact]
    public void GetLocks_marks_lock_orphaned_when_process_is_dead()
    {
        var id = Guid.NewGuid().ToString();
        var dir = Path.Combine(_stateDir, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "inuse.99999.lock"), "99999\n");

        var monitor = new SessionLockMonitor(_paths, new FakeProcessChecker(_ => false), NullLogger<SessionLockMonitor>.Instance);

        var locks = monitor.GetLocks(id);

        locks.Should().ContainSingle();
        locks[0].IsAlive.Should().BeFalse();
    }

    [Fact]
    public void GetLocks_returns_multiple_locks_for_concurrent_processes()
    {
        var id = Guid.NewGuid().ToString();
        var dir = Path.Combine(_stateDir, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "inuse.1001.lock"), "1001\n");
        File.WriteAllText(Path.Combine(dir, "inuse.1002.lock"), "1002\n");

        var monitor = new SessionLockMonitor(_paths, new FakeProcessChecker(pid => pid == 1001), NullLogger<SessionLockMonitor>.Instance);

        var locks = monitor.GetLocks(id);

        locks.Should().HaveCount(2);
        locks.Should().ContainSingle(l => l.ProcessId == 1001 && l.IsAlive);
        locks.Should().ContainSingle(l => l.ProcessId == 1002 && !l.IsAlive);
    }

    [Fact]
    public void GetLocks_skips_files_with_unparseable_pid()
    {
        var id = Guid.NewGuid().ToString();
        var dir = Path.Combine(_stateDir, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "inuse.notapid.lock"), "0");
        File.WriteAllText(Path.Combine(dir, "other.txt"), "ignore");

        var monitor = new SessionLockMonitor(_paths, new FakeProcessChecker(_ => true), NullLogger<SessionLockMonitor>.Instance);

        monitor.GetLocks(id).Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch (IOException)
            {
                // Tolerate transient locks on Windows.
            }
        }
        GC.SuppressFinalize(this);
    }

    private sealed class TestPaths : ICopilotPaths
    {
        public TestPaths(string dbPath, string stateDir)
        {
            SessionStoreDatabasePath = dbPath;
            SessionStateDirectory = stateDir;
        }

        public string SessionStoreDatabasePath { get; }
        public string SessionStateDirectory { get; }
    }

    private sealed class FakeProcessChecker : IProcessChecker
    {
        private readonly Func<int, bool> _isAlive;
        public FakeProcessChecker(Func<int, bool> isAlive) => _isAlive = isAlive;
        public bool IsAlive(int pid) => _isAlive(pid);
    }
}

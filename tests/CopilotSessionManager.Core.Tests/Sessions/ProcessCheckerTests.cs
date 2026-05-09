using System.Diagnostics;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;

namespace CopilotSessionManager.Core.Tests.Sessions;

public class ProcessCheckerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void IsAlive_returns_false_for_invalid_pid(int pid)
    {
        new ProcessChecker().IsAlive(pid).Should().BeFalse();
    }

    [Fact]
    public void IsAlive_returns_false_for_dead_pid()
    {
        // 0xFFFF (65535) is reserved by Windows and never maps to a user process.
        new ProcessChecker().IsAlive(0x7FFF_FFFE).Should().BeFalse();
    }

    [Fact]
    public void IsAlive_returns_true_for_current_process()
    {
        new ProcessChecker().IsAlive(Process.GetCurrentProcess().Id).Should().BeTrue();
    }
}

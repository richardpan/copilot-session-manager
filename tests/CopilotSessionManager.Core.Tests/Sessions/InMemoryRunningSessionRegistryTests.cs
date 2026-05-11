using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Sessions;

/// <summary>
/// Tests for the in-memory PID registry used by the open-session feature
/// (#104). Verifies the registry is a thin, thread-safe map without any
/// extra business logic.
/// </summary>
public class InMemoryRunningSessionRegistryTests
{
    [Fact]
    public void Register_Then_TryGet_ReturnsLastPid()
    {
        var sut = new InMemoryRunningSessionRegistry();
        sut.Register("session-1", 1234);

        sut.TryGetProcessId("session-1").Should().Be(1234);
    }

    [Fact]
    public void Register_OverwritesPreviousPid()
    {
        var sut = new InMemoryRunningSessionRegistry();
        sut.Register("session-1", 1234);
        sut.Register("session-1", 5678);

        sut.TryGetProcessId("session-1").Should().Be(5678);
    }

    [Fact]
    public void Unregister_RemovesEntry()
    {
        var sut = new InMemoryRunningSessionRegistry();
        sut.Register("session-1", 1234);
        sut.Unregister("session-1");

        sut.TryGetProcessId("session-1").Should().BeNull();
    }

    [Fact]
    public void TryGetProcessId_UnknownSession_ReturnsNull()
    {
        var sut = new InMemoryRunningSessionRegistry();
        sut.TryGetProcessId("ghost").Should().BeNull();
    }

    [Fact]
    public void Unregister_UnknownSession_DoesNotThrow()
    {
        var sut = new InMemoryRunningSessionRegistry();
        var act = () => sut.Unregister("ghost");
        act.Should().NotThrow();
    }

    [Fact]
    public void RegisterAndUnregister_AreCaseInsensitive()
    {
        var sut = new InMemoryRunningSessionRegistry();
        sut.Register("SESSION-ABC", 42);

        sut.TryGetProcessId("session-abc").Should().Be(42);

        sut.Unregister("Session-Abc");
        sut.TryGetProcessId("SESSION-ABC").Should().BeNull();
    }

    [Fact]
    public void Register_NonPositivePid_Throws()
    {
        var sut = new InMemoryRunningSessionRegistry();
        var actZero = () => sut.Register("session-1", 0);
        actZero.Should().Throw<System.ArgumentOutOfRangeException>();

        var actNegative = () => sut.Register("session-1", -42);
        actNegative.Should().Throw<System.ArgumentOutOfRangeException>();
    }
}

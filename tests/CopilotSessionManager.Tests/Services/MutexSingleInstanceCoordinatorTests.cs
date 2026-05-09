using System;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Services.SingleInstance;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Tests.Services;

public class MutexSingleInstanceCoordinatorTests
{
    private static string NewSuffix() => "test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task TryAcquireAsync_FirstCaller_Wins()
    {
        var suffix = NewSuffix();
        using var sut = new MutexSingleInstanceCoordinator(
            NullLogger<MutexSingleInstanceCoordinator>.Instance, suffix);

        var acquired = await sut.TryAcquireAsync();

        acquired.Should().BeTrue();
    }

    [Fact]
    public async Task TryAcquireAsync_SecondCallerInSameProcess_Fails()
    {
        var suffix = NewSuffix();
        using var owner = new MutexSingleInstanceCoordinator(
            NullLogger<MutexSingleInstanceCoordinator>.Instance, suffix);
        await owner.TryAcquireAsync();

        using var second = new MutexSingleInstanceCoordinator(
            NullLogger<MutexSingleInstanceCoordinator>.Instance, suffix);

        // A Win32 mutex is thread-affinitive: the same OS thread that
        // acquires it can re-enter. The real second-instance scenario is a
        // different process (and thus a different thread). Hop to a fresh
        // thread-pool worker to honour that.
        var acquired = await Task.Run(() => second.TryAcquireAsync());

        acquired.Should().BeFalse();
    }

    [Fact]
    public async Task SecondCaller_RaisesActivationRequested_OnOwner()
    {
        var suffix = NewSuffix();
        using var owner = new MutexSingleInstanceCoordinator(
            NullLogger<MutexSingleInstanceCoordinator>.Instance, suffix);
        (await owner.TryAcquireAsync()).Should().BeTrue();

        using var signal = new ManualResetEventSlim(initialState: false);
        owner.ActivationRequested += (_, _) => signal.Set();

        // Tiny delay so the listener task has its first NamedPipeServerStream
        // up before we try to connect.
        await Task.Delay(50);

        using var second = new MutexSingleInstanceCoordinator(
            NullLogger<MutexSingleInstanceCoordinator>.Instance, suffix);
        (await Task.Run(() => second.TryAcquireAsync())).Should().BeFalse();

        signal.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue(
            "the owner must receive an activation ping when a second instance probes");
    }

    [Fact]
    public async Task Dispose_ReleasesMutex_SoNewOwnerCanAcquire()
    {
        var suffix = NewSuffix();
        var first = new MutexSingleInstanceCoordinator(
            NullLogger<MutexSingleInstanceCoordinator>.Instance, suffix);
        (await first.TryAcquireAsync()).Should().BeTrue();
        first.Dispose();

        using var second = new MutexSingleInstanceCoordinator(
            NullLogger<MutexSingleInstanceCoordinator>.Instance, suffix);
        var acquired = await second.TryAcquireAsync();

        acquired.Should().BeTrue();
    }

    [Fact]
    public async Task TryAcquireAsync_AfterDispose_Throws()
    {
        var suffix = NewSuffix();
        var sut = new MutexSingleInstanceCoordinator(
            NullLogger<MutexSingleInstanceCoordinator>.Instance, suffix);
        sut.Dispose();

        await FluentActions.Invoking(() => sut.TryAcquireAsync())
            .Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public void Ctor_BlankSuffix_Throws()
    {
        FluentActions.Invoking(() => new MutexSingleInstanceCoordinator(
                NullLogger<MutexSingleInstanceCoordinator>.Instance, "   "))
            .Should().Throw<ArgumentException>();
    }
}

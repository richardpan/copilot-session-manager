using System;
using System.Runtime.Versioning;
using CopilotSessionManager.Services.Tray;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Tests.Services.Tray;

/// <summary>
/// Smoke tests for the real <see cref="NotifyIconTrayService"/>. These
/// touch <c>System.Windows.Forms.NotifyIcon</c> and so are gated to
/// Windows; CI runs Windows-only so they always execute there. Locally
/// they self-skip on non-Windows hosts.
/// </summary>
[SupportedOSPlatform("windows")]
public class NotifyIconTrayServiceTests
{
    [Fact]
    public void Show_then_dispose_is_safe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sut = new NotifyIconTrayService();
        sut.Show();
        sut.Hide();
        sut.Dispose();

        // Disposing twice must not throw.
        sut.Dispose();
    }

    [Fact]
    public void UpdateAwaitingInputCount_does_not_throw_for_typical_values()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sut = new NotifyIconTrayService();

        var act0 = () => sut.UpdateAwaitingInputCount(0);
        var act1 = () => sut.UpdateAwaitingInputCount(1);
        var actMany = () => sut.UpdateAwaitingInputCount(42);

        act0.Should().NotThrow();
        act1.Should().NotThrow();
        actMany.Should().NotThrow();
    }

    [Fact]
    public void Operations_after_dispose_throw_ObjectDisposedException()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sut = new NotifyIconTrayService();
        sut.Dispose();

        var show = () => sut.Show();
        var update = () => sut.UpdateAwaitingInputCount(1);

        show.Should().Throw<ObjectDisposedException>();
        update.Should().Throw<ObjectDisposedException>();
    }
}

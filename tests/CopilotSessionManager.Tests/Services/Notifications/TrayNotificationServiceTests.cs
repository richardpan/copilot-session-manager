using System;
using CopilotSessionManager.Services.Notifications;
using CopilotSessionManager.Services.Tray;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Tests.Services.Notifications;

public class TrayNotificationServiceTests
{
    [Fact]
    public void Show_ForwardsToTrayWithIsErrorFalseForInfo()
    {
        var tray = new RecordingTray();
        var sut = new TrayNotificationService(tray, NullLogger<TrayNotificationService>.Instance);

        sut.Show("Hello", "World");

        tray.Calls.Should().HaveCount(1);
        tray.Calls[0].title.Should().Be("Hello");
        tray.Calls[0].body.Should().Be("World");
        tray.Calls[0].isError.Should().BeFalse();
    }

    [Fact]
    public void Show_ErrorLevel_FlagsAsError()
    {
        var tray = new RecordingTray();
        var sut = new TrayNotificationService(tray, NullLogger<TrayNotificationService>.Instance);

        sut.Show("Bad", "Crash", NotificationLevel.Error);

        tray.Calls[0].isError.Should().BeTrue();
    }

    [Fact]
    public void Show_EmptyTitleAndBody_NoOps()
    {
        var tray = new RecordingTray();
        var sut = new TrayNotificationService(tray, NullLogger<TrayNotificationService>.Instance);

        sut.Show("   ", "");

        tray.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Show_TraySvcThrows_DoesNotPropagate()
    {
        var tray = new ThrowingTray();
        var sut = new TrayNotificationService(tray, NullLogger<TrayNotificationService>.Instance);

        var act = () => sut.Show("a", "b");
        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_NullDependencies_Throw()
    {
        var ctor1 = () => new TrayNotificationService(null!, NullLogger<TrayNotificationService>.Instance);
        var ctor2 = () => new TrayNotificationService(new RecordingTray(), null!);
        ctor1.Should().Throw<ArgumentNullException>();
        ctor2.Should().Throw<ArgumentNullException>();
    }

    private sealed class RecordingTray : ITrayIconService
    {
        public System.Collections.Generic.List<(string title, string body, bool isError)> Calls { get; } = new();

        public event EventHandler? ActivateRequested { add { } remove { } }
        public event EventHandler? OpenRequested { add { } remove { } }
        public event EventHandler? QuitRequested { add { } remove { } }

        public void Show() { }
        public void Hide() { }
        public void UpdateAwaitingInputCount(int count) { }
        public void ShowNotification(string title, string body, bool isError = false)
            => Calls.Add((title, body, isError));
        public void Dispose() { }
    }

    private sealed class ThrowingTray : ITrayIconService
    {
        public event EventHandler? ActivateRequested { add { } remove { } }
        public event EventHandler? OpenRequested { add { } remove { } }
        public event EventHandler? QuitRequested { add { } remove { } }

        public void Show() { }
        public void Hide() { }
        public void UpdateAwaitingInputCount(int count) { }
        public void ShowNotification(string title, string body, bool isError = false)
            => throw new InvalidOperationException("nope");
        public void Dispose() { }
    }
}

public class NoopNotificationServiceTests
{
    [Fact]
    public void Show_RecordsLastNotificationAndIncrementsCount()
    {
        var sut = new NoopNotificationService();
        sut.Show("title", "body", NotificationLevel.Error);
        sut.Show("t2", "b2");
        sut.CallCount.Should().Be(2);
        sut.LastNotification!.Value.Title.Should().Be("t2");
        sut.LastNotification.Value.Body.Should().Be("b2");
        sut.LastNotification.Value.Level.Should().Be(NotificationLevel.Info);
    }
}

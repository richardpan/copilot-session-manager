using System;
using System.Text;
using CopilotSessionManager.Terminal;
using FluentAssertions;

namespace CopilotSessionManager.Terminal.Tests;

public class ScreenBufferViewportInvalidatedTests
{
    [Fact]
    public void Fires_when_a_rune_is_printed()
    {
        var buffer = new ScreenBuffer(rows: 4, columns: 8);
        var fired = 0;
        buffer.ViewportInvalidated += (_, _) => fired++;

        buffer.Apply(new PrintRune(new Rune('a')));

        fired.Should().Be(1);
    }

    [Fact]
    public void Fires_once_per_Apply_call()
    {
        var buffer = new ScreenBuffer(rows: 4, columns: 8);
        var fired = 0;
        buffer.ViewportInvalidated += (_, _) => fired++;

        foreach (var b in Encoding.ASCII.GetBytes("hello"))
        {
            buffer.Apply(new PrintRune(new Rune((char)b)));
        }

        fired.Should().Be(5);
    }

    [Fact]
    public void Fires_when_cursor_position_changes_even_without_content_edit()
    {
        var buffer = new ScreenBuffer(rows: 4, columns: 8);
        var fired = 0;
        buffer.ViewportInvalidated += (_, _) => fired++;

        buffer.Apply(new SetCursorPosition(2, 3));

        fired.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Fires_when_Resize_changes_geometry()
    {
        var buffer = new ScreenBuffer(rows: 4, columns: 8);
        var fired = 0;
        buffer.ViewportInvalidated += (_, _) => fired++;

        buffer.Resize(6, 10);

        fired.Should().Be(1);
    }

    [Fact]
    public void Resize_to_same_size_is_a_noop_and_does_not_fire()
    {
        var buffer = new ScreenBuffer(rows: 4, columns: 8);
        var fired = 0;
        buffer.ViewportInvalidated += (_, _) => fired++;

        buffer.Resize(4, 8);

        fired.Should().Be(0);
    }

    [Fact]
    public void Reset_fires_invalidation()
    {
        var buffer = new ScreenBuffer(rows: 4, columns: 8);
        var fired = 0;
        buffer.ViewportInvalidated += (_, _) => fired++;

        buffer.Reset();

        fired.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Unsubscribing_stops_further_callbacks()
    {
        var buffer = new ScreenBuffer(rows: 4, columns: 8);
        var fired = 0;
        EventHandler handler = (_, _) => fired++;
        buffer.ViewportInvalidated += handler;

        buffer.Apply(new PrintRune(new Rune('a')));
        buffer.ViewportInvalidated -= handler;
        buffer.Apply(new PrintRune(new Rune('b')));

        fired.Should().Be(1);
    }
}

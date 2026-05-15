using System.Collections.Generic;
using System.Text;
using CopilotSessionManager.Terminal;
using FluentAssertions;

namespace CopilotSessionManager.Terminal.Tests;

internal static class ParseHelpers
{
    public static List<VtEvent> ParseAll(string ascii)
    {
        var events = new List<VtEvent>();
        var parser = new VtParser(events.Add);
        parser.Feed(Encoding.ASCII.GetBytes(ascii));
        return events;
    }

    public static List<VtEvent> ParseUtf8(string text)
    {
        var events = new List<VtEvent>();
        var parser = new VtParser(events.Add);
        parser.Feed(Encoding.UTF8.GetBytes(text));
        return events;
    }
}

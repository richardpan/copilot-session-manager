using System;
using System.Collections.Generic;
using System.IO;

namespace CopilotSessionManager.Tools.CapturePtyTrace;

/// <summary>Result of parsing the CapturePtyTrace command line.</summary>
internal sealed record CommandLineOptions(
    string CommandLine,
    string OutputPath,
    string? MetadataPath,
    short Columns,
    short Rows,
    string? WorkingDirectory,
    bool Mirror);

/// <summary>
/// Tiny argv parser. Splits on the first <c>--</c> separator: everything
/// before it is options, everything after is reassembled into the command
/// the child should run.
/// </summary>
internal static class CommandLineParser
{
    public static CommandLineOptions? Parse(string[] argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        var separator = Array.IndexOf(argv, "--");
        var optionTokens = separator < 0 ? argv : Span(argv, 0, separator);
        var commandTokens = separator < 0 ? Array.Empty<string>() : Span(argv, separator + 1, argv.Length - separator - 1);

        if (commandTokens.Length == 0)
            return null;

        string? outPath = null;
        string? metadataPath = null;
        string? cwd = null;
        short cols = 120;
        short rows = 30;
        var mirror = false;

        for (var i = 0; i < optionTokens.Length; i++)
        {
            var token = optionTokens[i];
            switch (token)
            {
                case "--out":
                    outPath = NextValue(optionTokens, ref i, token);
                    break;
                case "--metadata":
                    metadataPath = NextValue(optionTokens, ref i, token);
                    break;
                case "--cwd":
                    cwd = NextValue(optionTokens, ref i, token);
                    break;
                case "--cols":
                    cols = ParseShort(NextValue(optionTokens, ref i, token), token);
                    break;
                case "--rows":
                    rows = ParseShort(NextValue(optionTokens, ref i, token), token);
                    break;
                case "--mirror":
                    mirror = true;
                    break;
                case "--help":
                case "-h":
                    return null;
                default:
                    throw new ArgumentException($"Unknown option: {token}");
            }
        }

        outPath ??= Path.Combine(
            Directory.GetCurrentDirectory(),
            $"trace-{DateTime.UtcNow:yyyyMMdd-HHmmss}.bin");

        var commandLine = string.Join(' ', commandTokens);
        return new CommandLineOptions(commandLine, outPath, metadataPath, cols, rows, cwd, mirror);
    }

    private static string[] Span(string[] argv, int start, int length)
    {
        var copy = new string[length];
        Array.Copy(argv, start, copy, 0, length);
        return copy;
    }

    private static string NextValue(IReadOnlyList<string> tokens, ref int index, string flag)
    {
        if (index + 1 >= tokens.Count)
        {
            throw new ArgumentException($"{flag} requires a value");
        }
        return tokens[++index];
    }

    private static short ParseShort(string raw, string flag)
    {
        if (!short.TryParse(raw, out var v) || v <= 0)
        {
            throw new ArgumentException($"{flag} must be a positive integer (got '{raw}')");
        }
        return v;
    }
}

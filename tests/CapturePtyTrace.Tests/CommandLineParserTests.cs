using System;
using System.IO;
using CopilotSessionManager.Tools.CapturePtyTrace;
using FluentAssertions;

namespace CopilotSessionManager.Tools.CapturePtyTrace.Tests;

public class CommandLineParserTests
{
    [Fact]
    public void Parses_minimal_command_with_defaults()
    {
        var opts = CommandLineParser.Parse(new[] { "--", "cmd.exe", "/c", "echo", "hi" });

        opts.Should().NotBeNull();
        opts!.CommandLine.Should().Be("cmd.exe /c echo hi");
        opts.Columns.Should().Be(120);
        opts.Rows.Should().Be(30);
        opts.Mirror.Should().BeFalse();
        opts.OutputPath.Should().EndWith(".bin");
    }

    [Fact]
    public void Parses_explicit_options()
    {
        var opts = CommandLineParser.Parse(new[]
        {
            "--out", "out.bin",
            "--metadata", "out.json",
            "--cols", "100",
            "--rows", "40",
            "--cwd", "C:\\temp",
            "--mirror",
            "--", "cmd.exe", "/c", "ver",
        });

        opts.Should().NotBeNull();
        opts!.OutputPath.Should().Be("out.bin");
        opts.MetadataPath.Should().Be("out.json");
        opts.Columns.Should().Be(100);
        opts.Rows.Should().Be(40);
        opts.WorkingDirectory.Should().Be("C:\\temp");
        opts.Mirror.Should().BeTrue();
        opts.CommandLine.Should().Be("cmd.exe /c ver");
    }

    [Fact]
    public void Returns_null_when_no_command_specified()
    {
        CommandLineParser.Parse(Array.Empty<string>()).Should().BeNull();
        CommandLineParser.Parse(new[] { "--cols", "80" }).Should().BeNull();
        CommandLineParser.Parse(new[] { "--cols", "80", "--" }).Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_help_flags()
    {
        CommandLineParser.Parse(new[] { "--help", "--", "cmd" }).Should().BeNull();
        CommandLineParser.Parse(new[] { "-h", "--", "cmd" }).Should().BeNull();
    }

    [Fact]
    public void Throws_on_unknown_option()
    {
        var act = () => CommandLineParser.Parse(new[] { "--bogus", "--", "cmd" });
        act.Should().Throw<ArgumentException>().WithMessage("*--bogus*");
    }

    [Fact]
    public void Throws_when_value_missing_for_flag()
    {
        var act = () => CommandLineParser.Parse(new[] { "--cols", "--", "cmd" });
        act.Should().Throw<ArgumentException>().WithMessage("*--cols*requires*");
    }

    [Fact]
    public void Throws_when_cols_or_rows_not_positive()
    {
        var actCols = () => CommandLineParser.Parse(new[] { "--cols", "0", "--", "cmd" });
        actCols.Should().Throw<ArgumentException>().WithMessage("*--cols*positive*");

        var actRows = () => CommandLineParser.Parse(new[] { "--rows", "-1", "--", "cmd" });
        actRows.Should().Throw<ArgumentException>().WithMessage("*--rows*positive*");
    }
}

using System;
using CopilotSessionManager.Services;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Tests.Services;

/// <summary>
/// V1.5 (#194 follow-up): cover the resume-command-line helper used by
/// <see cref="DefaultTerminalSessionFactory.Create"/> so that the
/// embedded-tab launch path auto-attaches the Copilot CLI to the
/// requested session id instead of dropping the user at a bare pwsh
/// prompt. The factory itself is intentionally not exercised end-to-end
/// here because it spawns a real ConPTY process; the launch-string
/// construction is the only piece that benefits from unit coverage.
/// </summary>
public class DefaultTerminalSessionFactoryTests
{
    [Fact]
    public void BuildResumeCommandLine_PrefixesPwshAndPipesCopilotResume()
    {
        var actual = DefaultTerminalSessionFactory.BuildResumeCommandLine(
            "7b80dbcb-e779-44fa-82f8-dfa819c009d1");

        actual.Should().Be(
            "pwsh.exe -NoLogo -NoExit -Command \"copilot --resume '7b80dbcb-e779-44fa-82f8-dfa819c009d1'\"");
    }

    [Fact]
    public void BuildResumeCommandLine_EscapesEmbeddedSingleQuotes_PowerShellStyle()
    {
        // PowerShell's single-quoted string literal escape doubles the
        // embedded quote. Match what PowerShellSessionLauncher does for
        // the external launcher so embedded + external behave the same
        // for any contrived id the CLI might mint.
        var actual = DefaultTerminalSessionFactory.BuildResumeCommandLine("ab'cd");

        actual.Should().Be(
            "pwsh.exe -NoLogo -NoExit -Command \"copilot --resume 'ab''cd'\"");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildResumeCommandLine_RejectsNullOrWhitespaceSessionId(string? sessionId)
    {
        var act = () => DefaultTerminalSessionFactory.BuildResumeCommandLine(sessionId!);
        act.Should().Throw<ArgumentException>();
    }
}

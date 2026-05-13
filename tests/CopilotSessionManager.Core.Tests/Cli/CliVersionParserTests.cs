using CopilotSessionManager.Core.Cli;
using FluentAssertions;

namespace CopilotSessionManager.Core.Tests.Cli;

public class CliVersionParserTests
{
    [Theory]
    [InlineData("gh version 2.41.0 (2024-01-15)", "2.41.0")]
    [InlineData("gh version 2.41.0", "2.41.0")]
    [InlineData("v1.0.43", "1.0.43")]
    [InlineData("1.0.43-beta.1", "1.0.43")]
    [InlineData("2.0", "2.0.0")]
    [InlineData("copilot v0.5.4 (build 12345)", "0.5.4")]
    [InlineData("GitHub CLI version v2.40.1", "2.40.1")]
    public void TryParse_RecognizesSupportedFormats(string output, string expected)
    {
        var ok = CliVersionParser.TryParse(output, out var version);

        ok.Should().BeTrue();
        version.Should().Be(new Version(expected));
    }

    [Fact]
    public void TryParse_MultiLineOutput_UsesFirstLineThatLooksLikeVersion()
    {
        var output = string.Join(Environment.NewLine,
            "github.com/cli/cli",
            "gh version 2.42.1 (2024-02-01)",
            "https://github.com/cli/cli/releases/tag/v2.42.1");

        var ok = CliVersionParser.TryParse(output, out var version);

        ok.Should().BeTrue();
        version.Should().Be(new Version(2, 42, 1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("version unknown")]
    [InlineData("build 12345")]
    public void TryParse_Unparseable_ReturnsFalse(string output)
    {
        var ok = CliVersionParser.TryParse(output, out var version);

        ok.Should().BeFalse();
        version.Should().BeNull();
    }
}

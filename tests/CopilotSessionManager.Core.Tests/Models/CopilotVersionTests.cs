using CopilotSessionManager.Core.Models;
using FluentAssertions;

namespace CopilotSessionManager.Core.Tests.Models;

public class CopilotVersionTests
{
    [Theory]
    [InlineData("1.0.43", 1, 0, 43)]
    [InlineData("2.10.0", 2, 10, 0)]
    [InlineData("0.0.1", 0, 0, 1)]
    [InlineData(" 1.0.43 ", 1, 0, 43)]
    [InlineData("1.0.43-beta", 1, 0, 43)]
    [InlineData("1.0", 1, 0, 0)]
    [InlineData("1", 1, 0, 0)]
    public void TryParse_returns_expected_components(string input, int major, int minor, int patch)
    {
        CopilotVersion.TryParse(input, out var v).Should().BeTrue();
        v.Should().Be(new CopilotVersion(major, minor, patch));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("1.0.43.7")]
    [InlineData("-1.0.0")]
    [InlineData("a.b.c")]
    public void TryParse_returns_false_for_invalid(string? input)
    {
        CopilotVersion.TryParse(input, out _).Should().BeFalse();
    }

    [Fact]
    public void Parse_throws_on_invalid()
    {
        var act = () => CopilotVersion.Parse("nope");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Comparison_operators_order_by_components()
    {
        var a = new CopilotVersion(1, 0, 43);
        var aSame = new CopilotVersion(1, 0, 43);
        var b = new CopilotVersion(1, 1, 0);
        var c = new CopilotVersion(2, 0, 0);

        (a < b).Should().BeTrue();
        (b < c).Should().BeTrue();
        (a <= aSame).Should().BeTrue();
        (c >= b).Should().BeTrue();
        a.CompareTo(aSame).Should().Be(0);
    }

    [Fact]
    public void ToString_uses_invariant_culture_dot_separator()
    {
        var v = new CopilotVersion(1, 0, 43);
        v.ToString().Should().Be("1.0.43");
    }
}

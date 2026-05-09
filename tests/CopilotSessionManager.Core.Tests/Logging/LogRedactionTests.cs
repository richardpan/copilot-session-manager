using CopilotSessionManager.Core.Logging;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Logging;

public class LogRedactionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Redact_NullOrEmpty_ReturnsInput(string? input)
    {
        LogRedaction.Redact(input).Should().Be(input ?? string.Empty);
    }

    [Fact]
    public void Redact_BenignString_PassesThroughUnchanged()
    {
        const string s = "Started 3 sessions in C:\\ws\\repo on branch main.";
        LogRedaction.Redact(s).Should().Be(s);
    }

    [Theory]
    [InlineData("ghp_1234567890abcdefghijklmnopqrstuvwx")]
    [InlineData("gho_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa00")]
    [InlineData("ghs_qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq2")]
    [InlineData("ghu_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Redact_GitHubClassicTokens_AreScrubbed(string token)
    {
        var line = $"Authenticated with {token} successfully.";
        LogRedaction.Redact(line).Should().NotContain(token).And.Contain("[REDACTED]");
    }

    [Fact]
    public void Redact_GitHubFineGrainedPat_IsScrubbed()
    {
        const string token = "github_pat_11AABBCCDDEEFFGGHHIIJJ_KKLLMMNNOOPPQQRRSSTTUUVVWWXXYYZZ0011223344556677889900";
        var line = $"Header: token {token} expires later.";
        LogRedaction.Redact(line).Should().NotContain(token).And.Contain("[REDACTED]");
    }

    [Fact]
    public void Redact_BearerHeader_IsScrubbed()
    {
        const string line = "Authorization: Bearer abcDEF123456ghiJKL789012mnoPQR_-/+";
        var redacted = LogRedaction.Redact(line);
        redacted.Should().StartWith("Authorization: Bearer [REDACTED]");
        redacted.Should().NotContain("abcDEF123456");
    }

    [Fact]
    public void Redact_OpenAiKey_IsScrubbed()
    {
        const string token = "sk-proj-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789";
        var line = $"Configured OpenAI key={token}";
        var r = LogRedaction.Redact(line);
        r.Should().NotContain(token);
        r.Should().Contain("[REDACTED]");
    }

    [Fact]
    public void Redact_AnthropicKey_IsScrubbed()
    {
        const string token = "sk-ant-api03-AAAAAAAAAAAAAAAAAAAAAAAA";
        LogRedaction.Redact($"key={token}").Should().NotContain(token).And.Contain("[REDACTED]");
    }

    [Fact]
    public void Redact_AwsAccessKeyId_IsScrubbed()
    {
        const string key = "AKIAIOSFODNN7EXAMPLE";
        LogRedaction.Redact($"AWS_ACCESS_KEY_ID={key}").Should().NotContain(key);
    }

    [Fact]
    public void Redact_SlackToken_IsScrubbed()
    {
        // Build the token at runtime so the literal in source can't be flagged
        // by upstream secret scanners as a real Slack token.
        var token = string.Concat("xoxb", "-", "1234567890", "-", "abcdefghijklmnop");
        LogRedaction.Redact(token).Should().Be("[REDACTED]");
    }

    [Fact]
    public void Redact_Jwt_IsScrubbed()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NSIsIm5hbWUiOiJUZXN0In0.signaturepartABCDEFGH";
        LogRedaction.Redact($"cookie={jwt}").Should().NotContain(jwt).And.Contain("[REDACTED]");
    }

    [Theory]
    [InlineData("password=hunter2hunter2", "password=", "hunter2hunter2")]
    [InlineData("api_key=swordfishABCD", "api_key=", "swordfishABCD")]
    [InlineData("secret = my-super-secret-value", "secret = ", "my-super-secret-value")]
    public void Redact_KeyValueAssignments_AreScrubbed(string input, string expectedKeptPrefix, string secretValue)
    {
        var result = LogRedaction.Redact(input);
        result.Should().StartWith(expectedKeptPrefix);
        result.Should().NotContain(secretValue);
        result.Should().Contain("[REDACTED]");
    }

    [Fact]
    public void Redact_QuotedAssignment_DropsTheValue()
    {
        var result = LogRedaction.Redact("token: \"AbCdEf12345678\"");
        result.Should().NotContain("AbCdEf12345678");
        result.Should().Contain("[REDACTED]");
    }

    [Theory]
    [InlineData("apikey", true)]
    [InlineData("api_key", true)]
    [InlineData("API-KEY", true)]
    [InlineData("apiKey", true)]
    [InlineData("Prompt", true)]
    [InlineData("transcript", true)]
    [InlineData("RefreshToken", true)]
    [InlineData("repository", false)]
    [InlineData("turnCount", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSensitivePropertyName_HandlesCommonShapes(string? input, bool expected)
    {
        LogRedaction.IsSensitivePropertyName(input).Should().Be(expected);
    }

    [Fact]
    public void Redact_MultipleSecretsInOneLine_AllRedacted()
    {
        const string line = "ghp_111111111111111111111111111111111111 and Bearer abcDEFghiJKLmnoPQR1234567890";
        var r = LogRedaction.Redact(line);
        r.Should().NotContain("ghp_111111");
        r.Should().NotContain("abcDEFghi");
        r.Should().Contain(" and Bearer [REDACTED]");
    }
}

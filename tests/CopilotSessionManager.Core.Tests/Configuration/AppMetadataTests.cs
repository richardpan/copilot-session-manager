using CopilotSessionManager.Core.Configuration;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Configuration;

public class AppMetadataTests
{
    [Fact]
    public void ProductName_IsExpected()
    {
        AppMetadata.ProductName.Should().Be("Copilot Session Manager");
    }

    [Fact]
    public void SettingsSchemaVersion_IsPositive()
    {
        AppMetadata.SettingsSchemaVersion.Should().BeGreaterThan(0);
    }

    [Fact]
    public void DbSchemaVersion_IsPositive()
    {
        AppMetadata.DbSchemaVersion.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Version_IsResolvedAndNonEmpty()
    {
        AppMetadata.Version.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void MinSupportedCopilotCliVersion_IsParseable()
    {
        var parsed = System.Version.TryParse(
            AppMetadata.MinSupportedCopilotCliVersion,
            out _);
        parsed.Should().BeTrue();
    }
}

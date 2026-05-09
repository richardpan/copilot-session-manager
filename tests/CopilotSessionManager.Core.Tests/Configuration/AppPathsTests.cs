using System.IO;
using CopilotSessionManager.Core.Configuration;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Configuration;

public class AppPathsTests
{
    [Fact]
    public void LocalAppDataDirectory_EndsWithAppFolderName()
    {
        AppPaths.LocalAppDataDirectory
            .Should().EndWith(AppPaths.AppFolderName);
    }

    [Fact]
    public void LogsDirectory_IsUnderLocalAppDataDirectory()
    {
        AppPaths.LogsDirectory
            .Should().StartWith(AppPaths.LocalAppDataDirectory);
    }

    [Fact]
    public void AppDatabasePath_IsUnderLocalAppDataDirectory()
    {
        AppPaths.AppDatabasePath
            .Should().StartWith(AppPaths.LocalAppDataDirectory);
    }

    [Fact]
    public void CopilotSessionStateDirectory_IsUnderCopilotCliDirectory()
    {
        AppPaths.CopilotSessionStateDirectory
            .Should().StartWith(AppPaths.CopilotCliDirectory);
    }

    [Fact]
    public void CopilotCliDirectory_EndsWithDotCopilot()
    {
        Path.GetFileName(AppPaths.CopilotCliDirectory)
            .Should().Be(".copilot");
    }
}

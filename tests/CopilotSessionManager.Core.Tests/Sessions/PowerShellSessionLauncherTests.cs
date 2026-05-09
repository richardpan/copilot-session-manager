using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Sessions;

public class PowerShellSessionLauncherTests
{
    [Fact]
    public async Task LaunchAsync_BuildsCorrectInvocation()
    {
        var processes = new RecordingProcessLauncher(returnPid: 1234);
        var resolver = new FakeHostResolver(@"C:\Program Files\PowerShell\7\pwsh.exe");
        var sut = new PowerShellSessionLauncher(processes, resolver, NullLogger<PowerShellSessionLauncher>.Instance);

        // Use a guaranteed-existing dir so the launcher doesn't fall back.
        var existingCwd = Path.GetTempPath();
        var result = await sut.LaunchAsync("abc-123", existingCwd);

        result.ProcessId.Should().Be(1234);
        result.Executable.Should().Be(@"C:\Program Files\PowerShell\7\pwsh.exe");
        result.WorkingDirectory.Should().Be(existingCwd);
        result.Arguments.Should().Be("copilot --resume 'abc-123'");

        processes.Requests.Should().ContainSingle();
        var req = processes.Requests[0];
        req.FileName.Should().Be(@"C:\Program Files\PowerShell\7\pwsh.exe");
        req.Arguments.Should().BeEquivalentTo(new[] { "-NoExit", "-Command", "copilot --resume 'abc-123'" });
        req.WorkingDirectory.Should().Be(existingCwd);
        req.UseShellExecute.Should().BeTrue();
    }

    [Fact]
    public async Task LaunchAsync_FallsBackToUserProfile_WhenWorkingDirIsMissing()
    {
        var processes = new RecordingProcessLauncher(returnPid: 1);
        var resolver = new FakeHostResolver(@"C:\pwsh.exe");
        var sut = new PowerShellSessionLauncher(processes, resolver, NullLogger<PowerShellSessionLauncher>.Instance);

        await sut.LaunchAsync("s", workingDirectory: @"Z:\nope-not-here");

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        processes.Requests[0].WorkingDirectory.Should().Be(profile);
    }

    [Fact]
    public async Task LaunchAsync_FallsBackToUserProfile_WhenWorkingDirIsBlank()
    {
        var processes = new RecordingProcessLauncher(returnPid: 1);
        var resolver = new FakeHostResolver(@"C:\pwsh.exe");
        var sut = new PowerShellSessionLauncher(processes, resolver, NullLogger<PowerShellSessionLauncher>.Instance);

        await sut.LaunchAsync("s", workingDirectory: "   ");

        processes.Requests[0].WorkingDirectory
            .Should().Be(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    [Fact]
    public async Task LaunchAsync_NoHost_Throws()
    {
        var sut = new PowerShellSessionLauncher(
            new RecordingProcessLauncher(returnPid: null),
            new FakeHostResolver(host: null),
            NullLogger<PowerShellSessionLauncher>.Instance);

        await FluentActions.Invoking(() => sut.LaunchAsync("s"))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task LaunchAsync_EscapesSingleQuotesInSessionId()
    {
        var processes = new RecordingProcessLauncher(returnPid: 1);
        var sut = new PowerShellSessionLauncher(processes, new FakeHostResolver(@"C:\pwsh.exe"),
            NullLogger<PowerShellSessionLauncher>.Instance);

        await sut.LaunchAsync("weird'id", workingDirectory: Path.GetTempPath());

        processes.Requests[0].Arguments[2].Should().Be("copilot --resume 'weird''id'");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task LaunchAsync_RejectsBlankSessionId(string? id)
    {
        var sut = new PowerShellSessionLauncher(
            new RecordingProcessLauncher(returnPid: null),
            new FakeHostResolver(@"C:\pwsh.exe"),
            NullLogger<PowerShellSessionLauncher>.Instance);

        await FluentActions.Invoking(() => sut.LaunchAsync(id!))
            .Should().ThrowAsync<ArgumentException>();
    }

    private sealed class RecordingProcessLauncher : IProcessLauncher
    {
        private readonly int? _returnPid;
        public List<ProcessStartRequest> Requests { get; } = new();
        public RecordingProcessLauncher(int? returnPid) => _returnPid = returnPid;
        public int? Start(ProcessStartRequest request)
        {
            Requests.Add(request);
            return _returnPid;
        }
    }

    private sealed class FakeHostResolver : IPowerShellHostResolver
    {
        private readonly string? _host;
        public FakeHostResolver(string? host) => _host = host;
        public string? Resolve() => _host;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Onboarding;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Onboarding;

public class PrerequisiteCheckerTests
{
    private static PrerequisiteChecker BuildSut(
        FakeRunner runner,
        string? pwshPath = @"C:\fake\pwsh.exe",
        string? sessionStateDir = null)
    {
        var resolver = new FakeHostResolver(pwshPath);
        var paths = new FakePaths(sessionStateDir ?? Path.Combine(Path.GetTempPath(), "csm-pre-" + Guid.NewGuid().ToString("N")));
        return new PrerequisiteChecker(runner, resolver, paths, NullLogger<PrerequisiteChecker>.Instance);
    }

    [Fact]
    public async Task CheckAll_ReturnsExactlyFiveResultsInDisplayOrder()
    {
        var runner = new FakeRunner();
        var sut = BuildSut(runner, pwshPath: null);
        var results = await sut.CheckAllAsync();
        results.Should().HaveCount(5);
        results[0].Name.Should().Be("PowerShell 7+");
        results[1].Name.Should().Be("GitHub Copilot CLI");
        results[2].Name.Should().Be("GitHub CLI (gh)");
        results[3].Name.Should().Be("GitHub CLI authenticated");
        results[4].Name.Should().Be("Copilot session folder");
    }

    [Fact]
    public async Task PowerShell_NoHostFound_FailsWithInstallUrl()
    {
        var sut = BuildSut(new FakeRunner(), pwshPath: null);
        var r = (await sut.CheckAllAsync())[0];
        r.Status.Should().Be(PrerequisiteStatus.Failed);
        r.InstallUrl.Should().Be(PrerequisiteChecker.Urls.PowerShell);
    }

    [Fact]
    public async Task PowerShell_Version7Plus_Ok()
    {
        var runner = new FakeRunner()
            .OnAny(req => req.FileName.EndsWith("pwsh.exe"), new ProcessRunResult(0, "7.4.1\n", ""));
        var r = (await BuildSut(runner).CheckAllAsync())[0];
        r.Status.Should().Be(PrerequisiteStatus.Ok);
        r.InstallUrl.Should().BeNull();
        r.Detail.Should().Contain("7.4.1");
    }

    [Fact]
    public async Task PowerShell_Version5_Warns()
    {
        var runner = new FakeRunner()
            .OnAny(req => req.FileName.EndsWith("pwsh.exe"), new ProcessRunResult(0, "5.1.19041", ""));
        var r = (await BuildSut(runner).CheckAllAsync())[0];
        r.Status.Should().Be(PrerequisiteStatus.Warning);
        r.InstallUrl.Should().Be(PrerequisiteChecker.Urls.PowerShell);
    }

    [Fact]
    public async Task PowerShell_VersionUnparseable_Warns()
    {
        var runner = new FakeRunner()
            .OnAny(req => req.FileName.EndsWith("pwsh.exe"), new ProcessRunResult(0, "not a version", ""));
        var r = (await BuildSut(runner).CheckAllAsync())[0];
        r.Status.Should().Be(PrerequisiteStatus.Warning);
    }

    [Fact]
    public async Task PowerShell_NonZeroExit_Warns()
    {
        var runner = new FakeRunner()
            .OnAny(req => req.FileName.EndsWith("pwsh.exe"), new ProcessRunResult(1, "", "boom"));
        var r = (await BuildSut(runner).CheckAllAsync())[0];
        r.Status.Should().Be(PrerequisiteStatus.Warning);
    }

    [Fact]
    public async Task CopilotCli_NotFound_Fails()
    {
        var runner = new FakeRunner()
            .On("copilot", new[] { "--version" }, ProcessRunResult.NotFound);
        var r = (await BuildSut(runner).CheckAllAsync())[1];
        r.Status.Should().Be(PrerequisiteStatus.Failed);
        r.InstallUrl.Should().Be(PrerequisiteChecker.Urls.CopilotCli);
    }

    [Fact]
    public async Task CopilotCli_OkWhenVersionPresent()
    {
        var runner = new FakeRunner()
            .On("copilot", new[] { "--version" }, new ProcessRunResult(0, "copilot 0.5.4-beta", ""));
        var r = (await BuildSut(runner).CheckAllAsync())[1];
        r.Status.Should().Be(PrerequisiteStatus.Ok);
        r.Detail.Should().Contain("0.5.4");
    }

    [Fact]
    public async Task CopilotCli_EmptyVersion_Warns()
    {
        var runner = new FakeRunner()
            .On("copilot", new[] { "--version" }, new ProcessRunResult(0, "  \n", ""));
        var r = (await BuildSut(runner).CheckAllAsync())[1];
        r.Status.Should().Be(PrerequisiteStatus.Warning);
    }

    [Fact]
    public async Task GhCli_NotFound_Fails()
    {
        var runner = new FakeRunner()
            .On("gh", new[] { "--version" }, ProcessRunResult.NotFound);
        var r = (await BuildSut(runner).CheckAllAsync())[2];
        r.Status.Should().Be(PrerequisiteStatus.Failed);
        r.InstallUrl.Should().Be(PrerequisiteChecker.Urls.GhCli);
    }

    [Fact]
    public async Task GhCli_OkOnExitZero()
    {
        var runner = new FakeRunner()
            .On("gh", new[] { "--version" }, new ProcessRunResult(0, "gh version 2.50.0\nhttps://github.com/cli/cli", ""));
        var r = (await BuildSut(runner).CheckAllAsync())[2];
        r.Status.Should().Be(PrerequisiteStatus.Ok);
        r.Detail.Should().Contain("2.50");
    }

    [Fact]
    public async Task GhAuth_OkOnExitZero()
    {
        var runner = new FakeRunner()
            .On("gh", new[] { "auth", "status" }, new ProcessRunResult(0, "Logged in to github.com as richardpan", ""));
        var r = (await BuildSut(runner).CheckAllAsync())[3];
        r.Status.Should().Be(PrerequisiteStatus.Ok);
    }

    [Fact]
    public async Task GhAuth_FailsOnNonZeroExit()
    {
        var runner = new FakeRunner()
            .On("gh", new[] { "auth", "status" }, new ProcessRunResult(1, "", "You are not logged into any GitHub hosts."));
        var r = (await BuildSut(runner).CheckAllAsync())[3];
        r.Status.Should().Be(PrerequisiteStatus.Failed);
        r.InstallUrl.Should().Be(PrerequisiteChecker.Urls.GhAuth);
    }

    [Fact]
    public async Task GhAuth_FailsWhenGhNotInstalled()
    {
        var runner = new FakeRunner()
            .On("gh", new[] { "auth", "status" }, ProcessRunResult.NotFound);
        var r = (await BuildSut(runner).CheckAllAsync())[3];
        r.Status.Should().Be(PrerequisiteStatus.Failed);
    }

    [Fact]
    public async Task CopilotFolder_MissingDir_Warns()
    {
        var missing = Path.Combine(Path.GetTempPath(), "csm-missing-" + Guid.NewGuid().ToString("N"));
        var sut = BuildSut(new FakeRunner(), sessionStateDir: missing);
        var r = (await sut.CheckAllAsync())[4];
        r.Status.Should().Be(PrerequisiteStatus.Warning);
        r.InstallUrl.Should().Be(PrerequisiteChecker.Urls.CopilotFolder);
    }

    [Fact]
    public async Task CopilotFolder_WritableDir_Ok()
    {
        var dir = Path.Combine(Path.GetTempPath(), "csm-writable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var sut = BuildSut(new FakeRunner(), sessionStateDir: dir);
            var r = (await sut.CheckAllAsync())[4];
            r.Status.Should().Be(PrerequisiteStatus.Ok);
            r.InstallUrl.Should().BeNull();
            // Probe file should be cleaned up.
            Directory.GetFiles(dir, ".csm-write-probe.tmp").Should().BeEmpty();
        }
        finally
        {
            try
            { Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private sealed class FakeRunner : IProcessRunner
    {
        private readonly List<(Predicate<ProcessRunRequest> match, ProcessRunResult result)> _matchers = new();

        public FakeRunner On(string fileName, IReadOnlyList<string> args, ProcessRunResult result)
        {
            _matchers.Add((r => string.Equals(r.FileName, fileName, StringComparison.OrdinalIgnoreCase)
                && ArgsEqual(r.Arguments, args), result));
            return this;
        }

        public FakeRunner OnAny(Predicate<ProcessRunRequest> predicate, ProcessRunResult result)
        {
            _matchers.Add((predicate, result));
            return this;
        }

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
        {
            foreach (var (match, result) in _matchers)
            {
                if (match(request))
                {
                    return Task.FromResult(result);
                }
            }
            return Task.FromResult(ProcessRunResult.NotFound);
        }

        private static bool ArgsEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }
    }

    private sealed class FakeHostResolver : IPowerShellHostResolver
    {
        private readonly string? _host;
        public FakeHostResolver(string? host) => _host = host;
        public string? Resolve() => _host;
    }

    private sealed class FakePaths : ICopilotPaths
    {
        public FakePaths(string root) => SessionStateDirectory = root;
        public string SessionStoreDatabasePath => Path.Combine(SessionStateDirectory, "session-store.db");
        public string SessionStateDirectory { get; }
    }
}

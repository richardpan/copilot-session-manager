using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Cli.Share;
using CopilotSessionManager.Core.Onboarding;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Cli.Share;

public class CopilotShareInvokerTests : IDisposable
{
    private readonly List<string> _tempPaths = new();

    public void Dispose()
    {
        foreach (var p in _tempPaths)
        {
            try
            { if (File.Exists(p)) File.Delete(p); }
            catch { /* best-effort */ }
        }
    }

    private string NewTempPath()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "csm-share-" + Guid.NewGuid().ToString("N") + ".md");
        _tempPaths.Add(path);
        return path;
    }

    private CopilotShareInvoker BuildSut(FakeRunner runner, string? fixedTempPath = null)
    {
        return new CopilotShareInvoker(
            runner,
            NullLogger<CopilotShareInvoker>.Instance,
            executable: "copilot",
            timeoutSeconds: 30,
            tempFileFactory: fixedTempPath is null ? null : () => fixedTempPath);
    }

    [Fact]
    public async Task ExportAsync_BlankSessionId_ReturnsFailureWithoutInvokingCli()
    {
        var runner = new FakeRunner();
        var sut = BuildSut(runner);

        var result = await sut.ExportAsync("");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Session id");
        runner.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task ExportAsync_SuccessPath_ReturnsMarkdownAndPath()
    {
        var temp = NewTempPath();
        await File.WriteAllTextAsync(temp, "# Transcript\n\nhello world");
        var runner = new FakeRunner().Always(new ProcessRunResult(0, "", ""));
        var sut = BuildSut(runner, fixedTempPath: temp);

        var result = await sut.ExportAsync("session-abc");

        result.Success.Should().BeTrue();
        result.MarkdownPath.Should().Be(temp);
        result.Markdown.Should().Contain("hello world");
        result.ErrorMessage.Should().BeNull();

        // Args should match the documented contract.
        runner.Invocations.Should().HaveCount(1);
        var req = runner.Invocations[0];
        req.FileName.Should().Be("copilot");
        req.Arguments.Should().HaveCount(3);
        req.Arguments[0].Should().Be("--resume");
        req.Arguments[1].Should().Be("session-abc");
        req.Arguments[2].Should().Be($"--share={temp}");
        req.TimeoutSeconds.Should().Be(30);
    }

    [Fact]
    public async Task ExportAsync_NonZeroExit_ReturnsFailureAndDeletesTempFile()
    {
        var temp = NewTempPath();
        await File.WriteAllTextAsync(temp, "partial");
        var runner = new FakeRunner().Always(new ProcessRunResult(1, "", "boom: bad session id"));
        var sut = BuildSut(runner, fixedTempPath: temp);

        var result = await sut.ExportAsync("nope");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exited 1");
        result.ErrorMessage.Should().Contain("boom");
        File.Exists(temp).Should().BeFalse(because: "the invoker cleans up the temp file on failure");
    }

    [Fact]
    public async Task ExportAsync_Timeout_ReturnsTimeoutMessage()
    {
        var temp = NewTempPath();
        var runner = new FakeRunner().Always(new ProcessRunResult(-2, "", "timed out after 30s"));
        var sut = BuildSut(runner, fixedTempPath: temp);

        var result = await sut.ExportAsync("session-abc");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("timed out");
        result.ErrorMessage.Should().Contain("30");
    }

    [Fact]
    public async Task ExportAsync_CliMissing_ReturnsClassifiedError()
    {
        var temp = NewTempPath();
        var runner = new FakeRunner().Always(ProcessRunResult.NotFound);
        var sut = BuildSut(runner, fixedTempPath: temp);

        var result = await sut.ExportAsync("session-abc");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("copilot CLI not found");
    }

    [Fact]
    public async Task ExportAsync_FileNotProduced_ReturnsFailure()
    {
        // Point the factory at a path that we deliberately do not create.
        var temp = NewTempPath();
        if (File.Exists(temp))
            File.Delete(temp);

        var runner = new FakeRunner().Always(new ProcessRunResult(0, "", ""));
        var sut = BuildSut(runner, fixedTempPath: temp);

        var result = await sut.ExportAsync("session-abc");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("produced no output file");
    }

    [Fact]
    public async Task ExportAsync_EmptyFile_ReturnsFailureAndCleansUp()
    {
        var temp = NewTempPath();
        await File.WriteAllTextAsync(temp, "   \n\n");

        var runner = new FakeRunner().Always(new ProcessRunResult(0, "", ""));
        var sut = BuildSut(runner, fixedTempPath: temp);

        var result = await sut.ExportAsync("session-abc");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("empty transcript");
        File.Exists(temp).Should().BeFalse();
    }

    [Fact]
    public async Task ExportAsync_RunnerThrows_ReturnsFailureAndCleansUp()
    {
        var temp = NewTempPath();
        await File.WriteAllTextAsync(temp, "should not be read");

        var runner = new FakeRunner().Throw(new InvalidOperationException("kaboom"));
        var sut = BuildSut(runner, fixedTempPath: temp);

        var result = await sut.ExportAsync("session-abc");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("kaboom");
        File.Exists(temp).Should().BeFalse();
    }

    [Fact]
    public async Task ExportAsync_CancellationToken_PropagatesAndDeletesTemp()
    {
        var temp = NewTempPath();
        await File.WriteAllTextAsync(temp, "ignored");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var runner = new FakeRunner().Throw(new OperationCanceledException(cts.Token));
        var sut = BuildSut(runner, fixedTempPath: temp);

        Func<Task> act = async () => await sut.ExportAsync("session-abc", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(temp).Should().BeFalse();
    }

    [Fact]
    public void Constructor_RejectsNonPositiveTimeout()
    {
        var runner = new FakeRunner();
        FluentActions.Invoking(() =>
            new CopilotShareInvoker(
                runner,
                NullLogger<CopilotShareInvoker>.Instance,
                executable: "copilot",
                timeoutSeconds: 0,
                tempFileFactory: null))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void DefaultConstructor_UsesDocumentedDefaults()
    {
        var runner = new FakeRunner();
        var sut = new CopilotShareInvoker(runner, NullLogger<CopilotShareInvoker>.Instance);
        sut.Should().NotBeNull();
        CopilotShareInvoker.DefaultExecutable.Should().Be("copilot");
        CopilotShareInvoker.DefaultTimeoutSeconds.Should().Be(30);
    }

    private sealed class FakeRunner : IProcessRunner
    {
        private ProcessRunResult? _result;
        private Exception? _toThrow;
        public List<ProcessRunRequest> Invocations { get; } = new();

        public FakeRunner Always(ProcessRunResult result)
        {
            _result = result;
            return this;
        }

        public FakeRunner Throw(Exception ex)
        {
            _toThrow = ex;
            return this;
        }

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
        {
            Invocations.Add(request);
            if (_toThrow is not null)
            {
                throw _toThrow;
            }
            return Task.FromResult(_result ?? ProcessRunResult.NotFound);
        }
    }
}

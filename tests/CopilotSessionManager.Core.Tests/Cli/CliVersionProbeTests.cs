using CopilotSessionManager.Core.Cli;
using CopilotSessionManager.Core.Onboarding;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotSessionManager.Core.Tests.Cli;

public class CliVersionProbeTests
{
    private static readonly MinimumSupportedVersions Minimums = new(
        Gh: new Version(2, 40, 0),
        Copilot: new Version(1, 0, 0));

    [Fact]
    public async Task ProbeAsync_ReturnsVersionsForBothCliTools()
    {
        var runner = new ScriptedRunner()
            .With("gh", new ProcessRunResult(0, "gh version 2.41.0 (2024-01-15)", ""))
            .With("copilot", new ProcessRunResult(0, "v1.0.43", ""));
        var sut = new CliVersionProbe(runner, Minimums, NullLogger<CliVersionProbe>.Instance);

        var probes = await sut.ProbeAsync();

        probes.Should().HaveCount(2);
        probes[0].Cli.Should().Be("gh");
        probes[0].Detected.Should().Be(new Version(2, 41, 0));
        probes[0].IsOutdated.Should().BeFalse();
        probes[1].Cli.Should().Be("copilot");
        probes[1].Detected.Should().Be(new Version(1, 0, 43));
        probes[1].IsOutdated.Should().BeFalse();
    }

    [Fact]
    public async Task ProbeAsync_ClassifiesOlderGhAsOutdated()
    {
        var runner = new ScriptedRunner()
            .With("gh", new ProcessRunResult(0, "gh version 2.39.0", ""))
            .With("copilot", new ProcessRunResult(0, "1.0.0", ""));
        var sut = new CliVersionProbe(runner, Minimums, NullLogger<CliVersionProbe>.Instance);

        var gh = (await sut.ProbeAsync())[0];

        gh.Detected.Should().Be(new Version(2, 39, 0));
        gh.Minimum.Should().Be(new Version(2, 40, 0));
        gh.IsOutdated.Should().BeTrue();
    }

    [Fact]
    public async Task ProbeAsync_ClassifiesMissingCliAsOutdatedZeroVersion()
    {
        var runner = new ScriptedRunner()
            .With("gh", ProcessRunResult.NotFound)
            .With("copilot", new ProcessRunResult(0, "1.0.0", ""));
        var sut = new CliVersionProbe(runner, Minimums, NullLogger<CliVersionProbe>.Instance);

        var gh = (await sut.ProbeAsync())[0];

        gh.Detected.Should().Be(new Version(0, 0, 0));
        gh.IsOutdated.Should().BeTrue();
        gh.RawVersionLine.Should().Contain("not found");
    }

    [Fact]
    public async Task ProbeAsync_ClassifiesUnparseableOutputAsOutdatedZeroVersion()
    {
        var runner = new ScriptedRunner()
            .With("gh", new ProcessRunResult(0, "gh version bananas", ""))
            .With("copilot", new ProcessRunResult(0, "1.0.0", ""));
        var sut = new CliVersionProbe(runner, Minimums, NullLogger<CliVersionProbe>.Instance);

        var gh = (await sut.ProbeAsync())[0];

        gh.Detected.Should().Be(new Version(0, 0, 0));
        gh.IsOutdated.Should().BeTrue();
        gh.RawVersionLine.Should().Be("gh version bananas");
    }

    [Fact]
    public async Task ProbeAsync_UsesFiveSecondTimeoutPerCli()
    {
        var runner = new ScriptedRunner()
            .With("gh", new ProcessRunResult(0, "2.40.0", ""))
            .With("copilot", new ProcessRunResult(0, "1.0.0", ""));
        var sut = new CliVersionProbe(runner, Minimums, NullLogger<CliVersionProbe>.Instance);

        await sut.ProbeAsync();

        runner.Requests.Should().OnlyContain(request => request.TimeoutSeconds == 5);
        runner.Requests.Should().OnlyContain(request => request.Arguments.SequenceEqual(new[] { "--version" }));
    }

    [Fact]
    public async Task ProbeAsync_DoesNotSwallowCallerCancellation()
    {
        var runner = new ThrowingRunner(new OperationCanceledException());
        var sut = new CliVersionProbe(runner, Minimums, NullLogger<CliVersionProbe>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => sut.ProbeAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class ScriptedRunner : IProcessRunner
    {
        private readonly Dictionary<string, ProcessRunResult> _results = new(StringComparer.OrdinalIgnoreCase);

        public List<ProcessRunRequest> Requests { get; } = new();

        public ScriptedRunner With(string executable, ProcessRunResult result)
        {
            _results[executable] = result;
            return this;
        }

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(_results[request.FileName]);
        }
    }

    private sealed class ThrowingRunner(Exception exception) : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default) =>
            Task.FromException<ProcessRunResult>(exception);
    }
}
